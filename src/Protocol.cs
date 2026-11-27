using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace KilatDesk
{
    /// <summary>Message types exchanged after authentication. Frame = [type:1][length:4][payload].</summary>
    internal static class Msg
    {
        public const byte HostInfo = 1;     // H->V  host name, monitors
        public const byte VideoFormat = 2;  // H->V  codec, width, height, monitor, encoder name
        public const byte VideoFrame = 3;   // H->V  frameId, flags, data
        public const byte Cursor = 4;       // H->V  cursor shape id
        public const byte Clipboard = 5;    // both  UTF-8 text
        public const byte Ping = 6;         // both  timestamp
        public const byte Pong = 7;         // both  echoed timestamp
        public const byte Ack = 8;          // V->H  frameId fully shown
        public const byte HostStats = 9;    // H->V  current bitrate, auto flag, host flags (v2)
        public const byte Chat = 0x0A;      // both  UTF-8 text (v2)
        public const byte FileOffer = 0x0B; // both  id, name, size (v2)
        public const byte FileChunk = 0x0C; // both  id, data (v2)
        public const byte FileEnd = 0x0D;   // both  id, status 0=ok 1=cancel (v2)
        public const byte AudioFormat = 0x0E; // H->V sample rate, channels (v2)
        public const byte AudioData = 0x0F; // H->V IMA-ADPCM block (v2)
        public const byte Privacy = 0x17;   // V->H privacy screen on/off (v2)
        public const byte MouseMove = 0x10; // V->H  x,y normalized 0..65535
        public const byte MouseButton = 0x11;
        public const byte MouseWheel = 0x12;
        public const byte Key = 0x13;
        public const byte Settings = 0x14;  // V->H  bitrate, fps, scale, monitor, codec
        public const byte KeyframeReq = 0x15;
        public const byte ReleaseAll = 0x16;
        public const byte Bye = 0x20;
        public const int MaxPayload = 32 * 1024 * 1024;
    }

    /// <summary>
    /// One authenticated, encrypted connection. A dedicated sender thread batches queued messages into a single
    /// TLS write; a receiver thread raises OnMessage. Mouse moves are coalesced so slow links never lag behind.
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        readonly TcpClient _tcp;
        readonly Stream _s;
        public readonly string Remote;
        public event Action<byte, byte[]> OnMessage;
        public event Action<string> OnClosed;

        readonly object _q = new object();
        readonly Queue<byte[]> _queue = new Queue<byte[]>();
        readonly Queue<byte[]> _bulk = new Queue<byte[]>(); // file chunks: only sent when nothing else waits
        byte[] _pendingMove;
        public int BulkQueued { get { lock (_q) return _bulk.Count; } }
        long _queuedBytes;
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        int _closed;
        long _lastRecvTicks = NowMs;
        static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        public long BytesIn, BytesOut;
        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
        public bool IsClosed => _closed != 0;
        public string CloseReason { get; private set; }

        public Connection(TcpClient tcp, Stream secureStream, string remote)
        {
            _tcp = tcp; _s = secureStream; Remote = remote;
        }

        public void Start()
        {
            _tcp.Client.ReceiveTimeout = 0;
            _tcp.Client.SendTimeout = 0;
            new Thread(SendLoop) { IsBackground = true, Name = "net send" }.Start();
            new Thread(RecvLoop) { IsBackground = true, Name = "net recv" }.Start();
            new Thread(Watchdog) { IsBackground = true, Name = "net watchdog" }.Start();
        }

        static byte[] Frame(byte type, byte[] payload, int len)
        {
            var f = new byte[5 + len];
            f[0] = type;
            f[1] = (byte)len; f[2] = (byte)(len >> 8); f[3] = (byte)(len >> 16); f[4] = (byte)(len >> 24);
            if (len > 0) Buffer.BlockCopy(payload, 0, f, 5, len);
            return f;
        }

        public void Send(byte type, byte[] payload = null)
        {
            if (IsClosed) return;
            var f = Frame(type, payload, payload?.Length ?? 0);
            lock (_q)
            {
                if (_pendingMove != null) { _queue.Enqueue(_pendingMove); _queuedBytes += _pendingMove.Length; _pendingMove = null; }
                _queue.Enqueue(f);
                _queuedBytes += f.Length;
            }
            _signal.Set();
        }

        /// <summary>Low-priority message (file data): video, input and control always go first.</summary>
        public void SendBulk(byte type, byte[] payload)
        {
            if (IsClosed) return;
            var f = Frame(type, payload, payload?.Length ?? 0);
            lock (_q) _bulk.Enqueue(f);
            _signal.Set();
        }

        public void Send(byte type, Action<BinaryWriter> write)
        {
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms)) write(bw);
            Send(type, ms.ToArray());
        }

        /// <summary>Only the newest pending mouse position is kept.</summary>
        public void SendMouseMove(ushort x, ushort y)
        {
            if (IsClosed) return;
            var f = Frame(Msg.MouseMove, new[] { (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) }, 4);
            lock (_q) _pendingMove = f;
            _signal.Set();
        }

        void SendLoop()
        {
            var batch = new MemoryStream(64 * 1024);
            try
            {
                while (!IsClosed)
                {
                    _signal.WaitOne(500);
                    while (true)
                    {
                        batch.SetLength(0);
                        long taken = 0;
                        lock (_q)
                        {
                            while (_queue.Count > 0 && batch.Length < 512 * 1024)
                            {
                                var f = _queue.Dequeue();
                                batch.Write(f, 0, f.Length);
                                taken += f.Length;
                            }
                            if (_queue.Count == 0 && _pendingMove != null) { batch.Write(_pendingMove, 0, _pendingMove.Length); _pendingMove = null; }
                            if (batch.Length == 0 && _bulk.Count > 0) { var b = _bulk.Dequeue(); batch.Write(b, 0, b.Length); }
                        }
                        if (batch.Length == 0) break;
                        _s.Write(batch.GetBuffer(), 0, (int)batch.Length);
                        _s.Flush();
                        Interlocked.Add(ref _queuedBytes, -taken);
                        Interlocked.Add(ref BytesOut, batch.Length);
                    }
                }
            }
            catch (Exception ex) { Close("koneksi terputus (" + ex.GetType().Name + ")"); }
        }

        void ReadExact(byte[] buf, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n = _s.Read(buf, off, len - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        void RecvLoop()
        {
            var hdr = new byte[5];
            try
            {
                while (!IsClosed)
                {
                    ReadExact(hdr, 5);
                    int len = hdr[1] | (hdr[2] << 8) | (hdr[3] << 16) | (hdr[4] << 24);
                    if (len < 0 || len > Msg.MaxPayload) throw new InvalidDataException("pesan terlalu besar");
                    var payload = new byte[len];
                    if (len > 0) ReadExact(payload, len);
                    Interlocked.Add(ref BytesIn, len + 5);
                    Interlocked.Exchange(ref _lastRecvTicks, NowMs);
                    if (hdr[0] == Msg.Bye)
                    {
                        string why = "ditutup oleh sisi lain";
                        try { why = new BinaryReader(new MemoryStream(payload)).ReadString(); } catch { }
                        Close(why);
                        return;
                    }
                    if (hdr[0] == Msg.Ping) { Send(Msg.Pong, payload); continue; }
                    OnMessage?.Invoke(hdr[0], payload);
                }
            }
            catch (Exception ex)
            {
                if (!IsClosed) Log.Warn("recv: " + ex.Message);
                Close("koneksi terputus");
            }
        }

        void Watchdog()
        {
            while (!IsClosed)
            {
                Thread.Sleep(1000);
                Send(Msg.Ping, BitConverter.GetBytes(Stopwatch.GetTimestamp()));
                if (NowMs - Interlocked.Read(ref _lastRecvTicks) > 20000) Close("tidak ada respons (timeout)");
            }
        }

        /// <summary>Round-trip time from a Pong payload.</summary>
        public static double RttMs(byte[] pong) =>
            pong.Length >= 8 ? (Stopwatch.GetTimestamp() - BitConverter.ToInt64(pong, 0)) * 1000.0 / Stopwatch.Frequency : -1;

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            CloseReason = reason;
            _signal.Set();
            try { _s.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
            try { OnClosed?.Invoke(reason); } catch (Exception ex) { Log.Error("OnClosed", ex); }
        }

        /// <summary>Politely tells the other side why we leave, then closes.</summary>
        public void Bye(string reason)
        {
            if (IsClosed) return;
            lock (_q) { _queue.Clear(); _bulk.Clear(); _pendingMove = null; }
            Send(Msg.Bye, w => w.Write(reason ?? ""));
            var sw = Stopwatch.StartNew();
            while (!IsClosed && sw.ElapsedMilliseconds < 500)
            {
                lock (_q) if (_queue.Count == 0) break;
                Thread.Sleep(5);
            }
            Thread.Sleep(50);
            Close(reason);
        }

        public void Dispose() => Close("selesai");
    }
}
