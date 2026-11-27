using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace KilatDesk
{
    /// <summary>
    /// File transfer in both directions over the session connection. File data uses the low-priority (bulk) queue,
    /// so the picture and the mouse stay responsive while a file is being copied.
    /// Received files go to %USERPROFILE%\Downloads\KilatDesk.
    /// </summary>
    internal sealed class FileTransfer
    {
        const int ChunkSize = 32 * 1024;
        readonly Connection _conn;
        readonly Dictionary<int, Incoming> _in = new Dictionary<int, Incoming>();
        readonly HashSet<int> _failed = new HashSet<int>();
        readonly object _sendGate = new object();
        static int _ids = Environment.TickCount & 0xFFFFFF;
        volatile bool _cancel;
        int _sending;

        public event Action<string> Status;   // progress text (any thread)
        public event Action<string> Received; // full path of a completed incoming file
        public static string DownloadDirOverride;
        public static string DownloadDir => DownloadDirOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "KilatDesk");
        public bool IsSending => Volatile.Read(ref _sending) > 0;

        sealed class Incoming
        {
            public string Name, PartPath, FinalPath;
            public long Size, Done, LastReport;
            public FileStream Fs;
            public Stopwatch Sw;
        }

        public FileTransfer(Connection c)
        {
            _conn = c;
            c.OnClosed += r => AbortIncoming();
        }

        public void SendFiles(IEnumerable<string> paths)
        {
            var list = paths.Where(File.Exists).ToList();
            if (list.Count == 0) { Status?.Invoke("Folder belum didukung - pilih atau seret file."); return; }
            Interlocked.Increment(ref _sending);
            new Thread(() =>
            {
                try { foreach (var p in list) { if (_conn.IsClosed || _cancel) break; SendOne(p); } }
                catch (Exception ex) { Status?.Invoke("Gagal mengirim: " + ex.Message); }
                finally { Interlocked.Decrement(ref _sending); _cancel = false; }
            }) { IsBackground = true, Name = "file send" }.Start();
        }

        public void CancelSending() { if (IsSending) _cancel = true; }

        void SendOne(string path)
        {
            lock (_sendGate)
            {
                var fi = new FileInfo(path);
                int id = Interlocked.Increment(ref _ids);
                _conn.Send(Msg.FileOffer, w => { w.Write(id); w.Write(fi.Name); w.Write(fi.Length); });
                var sw = Stopwatch.StartNew();
                long done = 0, last = 0;
                bool ok = true;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                {
                    var buf = new byte[ChunkSize];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        while (_conn.BulkQueued >= 8 && !_conn.IsClosed && !_cancel) Thread.Sleep(1);
                        if (_conn.IsClosed || _cancel) { ok = false; break; }
                        var p = new byte[4 + n];
                        BitConverter.GetBytes(id).CopyTo(p, 0);
                        Buffer.BlockCopy(buf, 0, p, 4, n);
                        _conn.SendBulk(Msg.FileChunk, p);
                        done += n;
                        if (sw.ElapsedMilliseconds - last > 300) { last = sw.ElapsedMilliseconds; Status?.Invoke(Progress("Mengirim", fi.Name, done, fi.Length, sw)); }
                    }
                }
                var end = new byte[5];
                BitConverter.GetBytes(id).CopyTo(end, 0);
                end[4] = (byte)(ok ? 0 : 1);
                _conn.SendBulk(Msg.FileEnd, end); // bulk too, so it arrives after the last chunk
                while (_conn.BulkQueued > 0 && !_conn.IsClosed) Thread.Sleep(5);
                Status?.Invoke(ok && !_conn.IsClosed ? "Terkirim: " + fi.Name + " (" + Size(fi.Length) + ")" : "Dibatalkan: " + fi.Name);
            }
        }

        /// <summary>Returns true when the message belonged to file transfer.</summary>
        public bool Handle(byte type, byte[] d)
        {
            try
            {
                switch (type)
                {
                    case Msg.FileOffer:
                        using (var br = new BinaryReader(new MemoryStream(d)))
                        {
                            int id = br.ReadInt32(); string name = br.ReadString(); long size = br.ReadInt64();
                            try { Begin(id, name, size); }
                            catch (Exception ex) { lock (_in) _failed.Add(id); Status?.Invoke("Tidak bisa menyimpan " + name + ": " + ex.Message); }
                        }
                        return true;
                    case Msg.FileChunk:
                        {
                            int id = BitConverter.ToInt32(d, 0);
                            Incoming f;
                            lock (_in) if (!_in.TryGetValue(id, out f)) return true;
                            try { f.Fs.Write(d, 4, d.Length - 4); }
                            catch (Exception ex) { Fail(id, f, ex.Message); return true; }
                            f.Done += d.Length - 4;
                            if (f.Sw.ElapsedMilliseconds - f.LastReport > 300) { f.LastReport = f.Sw.ElapsedMilliseconds; Status?.Invoke(Progress("Menerima", f.Name, f.Done, f.Size, f.Sw)); }
                            return true;
                        }
                    case Msg.FileEnd:
                        Finish(BitConverter.ToInt32(d, 0), d.Length > 4 && d[4] == 0);
                        return true;
                }
            }
            catch (Exception ex) { Log.Warn("file transfer: " + ex.Message); return true; }
            return false;
        }

        void Begin(int id, string name, long size)
        {
            string dir = DownloadDir;
            Directory.CreateDirectory(dir);
            string safe = Sanitize(name);
            string final = Unique(Path.Combine(dir, safe));
            var inc = new Incoming { Name = safe, FinalPath = final, PartPath = final + ".part", Size = size, Sw = Stopwatch.StartNew() };
            inc.Fs = new FileStream(inc.PartPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            lock (_in) _in[id] = inc;
            Status?.Invoke("Menerima " + safe + " (" + Size(size) + ") ...");
        }

        void Fail(int id, Incoming f, string why)
        {
            lock (_in) { _in.Remove(id); _failed.Add(id); }
            try { f.Fs.Dispose(); File.Delete(f.PartPath); } catch { }
            Status?.Invoke("Gagal menyimpan " + f.Name + ": " + why);
        }

        void Finish(int id, bool ok)
        {
            Incoming f;
            lock (_in)
            {
                _failed.Remove(id);
                if (!_in.TryGetValue(id, out f)) return;
                _in.Remove(id);
            }
            f.Fs.Dispose();
            if (ok && f.Done == f.Size)
            {
                if (File.Exists(f.FinalPath)) f.FinalPath = Unique(f.FinalPath);
                File.Move(f.PartPath, f.FinalPath);
                Status?.Invoke("Diterima: " + f.Name + " (" + Size(f.Size) + ") -> " + f.FinalPath);
                Received?.Invoke(f.FinalPath);
            }
            else
            {
                try { File.Delete(f.PartPath); } catch { }
                Status?.Invoke("Transfer dibatalkan: " + f.Name);
            }
        }

        void AbortIncoming()
        {
            lock (_in)
            {
                foreach (var f in _in.Values) { try { f.Fs.Dispose(); File.Delete(f.PartPath); } catch { } }
                _in.Clear();
            }
        }

        static string Sanitize(string name)
        {
            name = Path.GetFileName((name ?? "").Replace('/', '\\'));
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0) name = "file";
            string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" }.Contains(stem)) name = "_" + name;
            if (name.Length > 150) name = name.Substring(name.Length - 150);
            return name;
        }

        static string Unique(string path)
        {
            if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p) && !File.Exists(p + ".part")) return p;
            }
        }

        public static string Size(long b) =>
            b >= 1L << 30 ? (b / 1073741824.0).ToString("0.00") + " GB" :
            b >= 1L << 20 ? (b / 1048576.0).ToString("0.0") + " MB" : (b / 1024.0).ToString("0") + " KB";

        static string Progress(string verb, string name, long done, long total, Stopwatch sw)
        {
            double pct = total > 0 ? done * 100.0 / total : 100;
            double mbs = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            return verb + " " + name + ": " + pct.ToString("0") + "% (" + Size(done) + " / " + Size(total) + ", " + mbs.ToString("0.0") + " MB/s)";
        }
    }
}
