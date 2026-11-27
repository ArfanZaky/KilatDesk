using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KilatDesk
{
    /// <summary>Viewer side logic without UI: receives + decodes video into a BGRA frame buffer, sends input.</summary>
    internal sealed unsafe class ViewerSession : IDisposable
    {
        public readonly Connection Conn;
        public readonly FileTransfer Files;
        public event Action HostInfoReceived, FormatChanged, FrameDecoded, HostStatsReceived;
        public event Action<byte> CursorChanged;
        public event Action<string> ClipboardReceived, Closed, ChatReceived;

        public string HostName = "?";
        public string[] MonitorNames = new string[0];
        public List<MonitorInfo> MonitorList = new List<MonitorInfo>();
        public int Monitor;
        public byte Features;          // HostFeatures bits (0 = version 1 host)
        public string EncoderName = "", CaptureKind = "";
        public byte Codec;

        public readonly object FrameLock = new object();
        public IntPtr Frame;
        public int Width, Height;
        IFrameDecoder _dec;
        readonly bool _canH264;

        public int Kbps, Fps, Scale, MonitorRequest = -1;
        public byte CodecPref;
        public bool AutoBitrate, Audio;
        public volatile bool ViewOnly;

        public double Rtt = -1, FpsShown, MbpsIn;
        public int FramesTotal, HostKbps, HostFps;
        public bool HostAuto, HostInputAllowed = true, HostPrivacy, HasHostStats;
        int _frames; long _lastBytes; long _lastStat = Environment.TickCount; long _lastKeyReq;
        AudioPlayer _player;
        readonly object _audioGate = new object();
        public int AudioRate, AudioPackets;
        public static string ScreenshotDirOverride;
        public string RemoteKey => Conn.Remote;

        public ViewerSession(Connection c)
        {
            Conn = c;
            Files = new FileTransfer(c);
            _canH264 = H264Decoder.IsAvailable();
            Kbps = Settings.GetInt("viewer.kbps", 8000);
            Fps = Settings.GetInt("viewer.fps", 60);
            Scale = Settings.GetInt("viewer.scale", 100);
            CodecPref = (byte)Settings.GetInt("viewer.codec", CodecId.H264);
            AutoBitrate = Settings.GetInt("viewer.auto", 1) != 0;
            Audio = Settings.GetInt("viewer.audio", 1) != 0;
            MonitorRequest = Settings.GetInt("viewer.mon." + RemoteKey, -1);
            c.OnMessage += OnMessage;
            c.OnClosed += r => { StopAudio(); Closed?.Invoke(r); };
        }

        public bool Has(byte feature) => (Features & feature) != 0;

        public void Start() { Conn.Start(); SendSettings(); }

        /// <param name="remember">true when the user picked the value, so it becomes the default next time.</param>
        public void SendSettings(bool remember = false)
        {
            Conn.Send(Msg.Settings, w =>
            {
                w.Write(Kbps); w.Write(Fps); w.Write(Scale); w.Write(MonitorRequest);
                w.Write(CodecPref); w.Write((byte)(_canH264 ? 1 : 0));
                w.Write((byte)(AutoBitrate ? 1 : 0)); w.Write((byte)(Audio ? 1 : 0)); // v2 (ignored by v1 hosts)
            });
            if (!Audio) StopAudio();
            if (!remember) return;
            Settings.Set("viewer.kbps", Kbps.ToString()); Settings.Set("viewer.fps", Fps.ToString());
            Settings.Set("viewer.scale", Scale.ToString()); Settings.Set("viewer.codec", CodecPref.ToString());
            Settings.Set("viewer.auto", AutoBitrate ? "1" : "0"); Settings.Set("viewer.audio", Audio ? "1" : "0");
        }

        public void RequestMonitor(int index)
        {
            MonitorRequest = index;
            Settings.Set("viewer.mon." + RemoteKey, index.ToString());
            SendSettings();
        }

        /// <summary>Call about once per second.</summary>
        public void UpdateStats()
        {
            long now = Environment.TickCount, dt = Math.Max(1, now - _lastStat);
            long bytes = Interlocked.Read(ref Conn.BytesIn);
            FpsShown = Interlocked.Exchange(ref _frames, 0) * 1000.0 / dt;
            MbpsIn = (bytes - _lastBytes) * 8.0 / dt / 1000.0;
            _lastBytes = bytes; _lastStat = now;
        }

        void OnMessage(byte type, byte[] d)
        {
            if (Files.Handle(type, d)) return;
            switch (type)
            {
                case Msg.HostInfo:
                    using (var br = new BinaryReader(new MemoryStream(d)))
                    {
                        HostName = br.ReadString();
                        var names = new string[br.ReadInt32()];
                        for (int i = 0; i < names.Length; i++) names[i] = br.ReadString();
                        int current = br.ReadInt32();
                        var list = new List<MonitorInfo>();
                        byte features = 0;
                        if (br.BaseStream.Position < br.BaseStream.Length) // v2 host
                        {
                            br.ReadInt32(); // extension version
                            features = br.ReadByte();
                            for (int i = 0; i < names.Length; i++)
                                list.Add(new MonitorInfo { Name = "Monitor " + (i + 1), X = br.ReadInt32(), Y = br.ReadInt32(), Width = br.ReadInt32(), Height = br.ReadInt32(), Primary = br.ReadByte() != 0 });
                        }
                        MonitorNames = names; MonitorList = list; Features = features; Monitor = current;
                    }
                    HostInfoReceived?.Invoke();
                    break;
                case Msg.VideoFormat:
                    using (var br = new BinaryReader(new MemoryStream(d)))
                    {
                        byte codec = br.ReadByte(); int w = br.ReadInt32(), h = br.ReadInt32(), mon = br.ReadInt32();
                        string enc = br.ReadString(), cap = br.ReadString();
                        lock (FrameLock)
                        {
                            _dec?.Dispose(); _dec = null;
                            if (Frame != IntPtr.Zero) Marshal.FreeHGlobal(Frame);
                            Frame = Marshal.AllocHGlobal(w * h * 4);
                            uint* px = (uint*)Frame;
                            for (long i = 0, n = (long)w * h; i < n; i++) px[i] = 0xFF000000;
                            Width = w; Height = h; Codec = codec;
                            try { _dec = Codecs.CreateDecoder(codec, w, h); }
                            catch (Exception ex) { Log.Error("decoder", ex); Conn.Bye("Viewer tidak bisa membuat decoder " + CodecId.Name(codec)); return; }
                        }
                        Monitor = mon; EncoderName = enc; CaptureKind = cap;
                    }
                    FormatChanged?.Invoke();
                    break;
                case Msg.VideoFrame:
                    if (d.Length < 5) break;
                    bool ok = false;
                    lock (FrameLock)
                    {
                        if (_dec != null)
                        {
                            try { ok = _dec.Decode(d, 5, d.Length - 5, (byte*)Frame, Width * 4); }
                            catch (Exception ex) { Log.Warn("decode: " + ex.Message); RequestKeyframe(); }
                        }
                    }
                    if (ok) { Interlocked.Increment(ref _frames); FramesTotal++; FrameDecoded?.Invoke(); }
                    Conn.Send(Msg.Ack, BitConverter.GetBytes(BitConverter.ToInt32(d, 0)));
                    break;
                case Msg.HostStats:
                    using (var br = new BinaryReader(new MemoryStream(d)))
                    {
                        HostKbps = br.ReadInt32(); HostAuto = br.ReadByte() != 0;
                        byte f = br.ReadByte(); HostFps = br.ReadInt32();
                        HostInputAllowed = (f & 1) != 0; HostPrivacy = (f & 2) != 0; HasHostStats = true;
                    }
                    HostStatsReceived?.Invoke();
                    break;
                case Msg.AudioFormat:
                    using (var br = new BinaryReader(new MemoryStream(d)))
                    {
                        int rate = br.ReadInt32(), ch = br.ReadByte();
                        AudioRate = rate;
                        lock (_audioGate)
                        {
                            _player?.Dispose(); _player = null;
                            if (Audio) { try { _player = new AudioPlayer(rate, ch); } catch (Exception ex) { Log.Warn("audio player: " + ex.Message); } }
                        }
                    }
                    break;
                case Msg.AudioData:
                    AudioPackets++;
                    lock (_audioGate)
                    {
                        if (_player == null || !Audio) break;
                        var pcm = Adpcm.Decode(d, 0, out _, out int frames);
                        if (frames > 0) _player.Feed(pcm);
                    }
                    break;
                case Msg.Cursor: if (d.Length > 0) CursorChanged?.Invoke(d[0]); break;
                case Msg.Clipboard: ClipboardReceived?.Invoke(Encoding.UTF8.GetString(d)); break;
                case Msg.Chat: ChatReceived?.Invoke(Encoding.UTF8.GetString(d)); break;
                case Msg.Pong: Rtt = Connection.RttMs(d); break;
            }
        }

        void StopAudio() { lock (_audioGate) { _player?.Dispose(); _player = null; } }

        void RequestKeyframe()
        {
            long now = Environment.TickCount;
            if (now - _lastKeyReq < 500) return;
            _lastKeyReq = now;
            Conn.Send(Msg.KeyframeReq);
        }

        public void MouseMove(ushort x, ushort y) { if (!ViewOnly) Conn.SendMouseMove(x, y); }
        public void MouseButton(byte b, bool down) { if (!ViewOnly) Conn.Send(Msg.MouseButton, new[] { b, (byte)(down ? 1 : 0) }); }
        public void Wheel(short delta, bool horizontal) { if (!ViewOnly) Conn.Send(Msg.MouseWheel, new[] { (byte)delta, (byte)(delta >> 8), (byte)(horizontal ? 1 : 0) }); }
        public void Key(ushort vk, ushort scan, bool ext, bool up)
        {
            if (!ViewOnly) Conn.Send(Msg.Key, new[] { (byte)vk, (byte)(vk >> 8), (byte)scan, (byte)(scan >> 8), (byte)((up ? 1 : 0) | (ext ? 2 : 0)) });
        }
        public void ReleaseAll() => Conn.Send(Msg.ReleaseAll);
        public void SendClipboard(string t) => Conn.Send(Msg.Clipboard, Encoding.UTF8.GetBytes(t));
        public void SendChat(string t) => Conn.Send(Msg.Chat, Encoding.UTF8.GetBytes(t));
        public void SetPrivacy(bool on) => Conn.Send(Msg.Privacy, new[] { (byte)(on ? 1 : 0) });

        /// <summary>Presses a key combination on the remote PC (all down in order, then up in reverse).</summary>
        public void SendCombo(params ushort[] vks)
        {
            foreach (var vk in vks) Key(vk, (ushort)Native.MapVirtualKeyW(vk, 0), IsExtended(vk), false);
            for (int i = vks.Length - 1; i >= 0; i--) Key(vks[i], (ushort)Native.MapVirtualKeyW(vks[i], 0), IsExtended(vks[i]), true);
        }

        static bool IsExtended(ushort vk) =>
            vk == 0x5B || vk == 0x5C || vk == 0x5D || (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0xA3 || vk == 0xA5 || vk == 0x2C;

        /// <summary>Saves the current remote picture as PNG. Returns the file path.</summary>
        public string SaveScreenshot()
        {
            string dir = ScreenshotDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KilatDesk");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "KilatDesk_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            lock (FrameLock)
            {
                if (Frame == IntPtr.Zero || Width == 0) throw new InvalidOperationException("Belum ada gambar.");
                using (var bmp = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppRgb, Frame)) bmp.Save(path, ImageFormat.Png);
            }
            return path;
        }

        public void Dispose()
        {
            Conn.Bye("Viewer ditutup");
            StopAudio();
            lock (FrameLock)
            {
                _dec?.Dispose(); _dec = null;
                if (Frame != IntPtr.Zero) { Marshal.FreeHGlobal(Frame); Frame = IntPtr.Zero; }
                Width = Height = 0;
            }
        }
    }

    /// <summary>Reverse mode: the viewer waits on a port and the host connects to it.</summary>
    internal sealed class ViewerListener
    {
        TcpListener _l;
        ListenerSecret _secret;
        public event Action<Connection> Connected;
        public event Action<string> Status;
        public bool IsListening => _l != null;

        public void Start(int port, string password)
        {
            Stop();
            HostService.EnsurePortFree(port);
            _secret = new ListenerSecret(password);
            var l = new TcpListener(HostService.TestLoopbackOnly ? IPAddress.Loopback : IPAddress.IPv6Any, port);
            if (!HostService.TestLoopbackOnly) l.Server.DualMode = true;
            l.Start();
            _l = l;
            new Thread(() =>
            {
                while (_l == l)
                {
                    TcpClient c;
                    try { c = l.AcceptTcpClient(); } catch { break; }
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        try { Connected?.Invoke(Secure.Accept(c, AppRole.Viewer, _secret, remote => Secure.R_OK)); }
                        catch (AuthException ex) { Status?.Invoke("Koneksi masuk ditolak: " + ex.Message); }
                        catch (Exception ex) { Log.Warn("viewer listener: " + ex.Message); }
                    });
                }
            }) { IsBackground = true, Name = "viewer accept" }.Start();
        }

        public void Stop()
        {
            var l = _l; _l = null;
            try { l?.Stop(); } catch { }
        }
    }
}
