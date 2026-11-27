using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KilatDesk
{
    /// <summary>Host feature bits announced to the viewer (v2).</summary>
    internal static class HostFeatures
    {
        public const byte AllMonitors = 1, Files = 2, Chat = 4, Audio = 8, Privacy = 16, AutoBitrate = 32;
    }

    /// <summary>Simple AIMD bitrate control: back off quickly when frames queue up, creep up when the link is calm.</summary>
    internal sealed class BitrateController
    {
        int _congestion, _calm, _max;
        public int Current;
        public const int Min = 800;
        public BitrateController(int start, int max) { _max = Math.Max(Min, max); Current = Math.Max(Min, Math.Min(start, _max)); }
        public void NoteCongestion() => Interlocked.Increment(ref _congestion);

        /// <summary>Call once per second. Returns true when Current changed.</summary>
        public bool Tick(bool active)
        {
            int c = Interlocked.Exchange(ref _congestion, 0), old = Current;
            if (c >= 25) { Current = Math.Max(Min, (int)(Current * 0.7)); _calm = 0; }  // >= ~100 ms blocked in that second
            else if (c == 0 && active) { if (++_calm >= 2) Current = Math.Min(_max, (int)(Current * 1.15) + 250); }
            else _calm = 0;
            return Current != old;
        }
    }

    /// <summary>
    /// One remote-control session on the controlled PC: capture -> encode -> send, plus input injection.
    /// Latency control: frames are skipped at the source when they start to queue, so a slow link never builds lag.
    /// </summary>
    internal sealed unsafe class HostSession
    {
        /// <summary>Host owner's choice: may the viewer use mouse/keyboard?</summary>
        public static volatile bool AllowInput = true;
        /// <summary>Privacy screen needs WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 / build 19041+).</summary>
        public static readonly bool PrivacySupported = Environment.OSVersion.Version.Build >= 19041;

        readonly Connection _conn;
        readonly InputInjector _input = new InputInjector();
        List<MonitorInfo> _monitors;
        volatile int _kbps = 8000, _fps = 60, _scale = 100, _monitor = -1;
        volatile byte _codecPref = CodecId.H264;
        volatile bool _viewerH264 = true, _reconfigure = true, _bitrateDirty, _keyRequested, _stop, _autoBitrate, _audioWanted;
        int _lastSent, _lastAcked, _framesThisSecond;
        readonly long[] _sentAt = new long[64];
        readonly Queue<KeyValuePair<long, double>> _lat = new Queue<KeyValuePair<long, double>>();
        double _baseLatency = -1;
        readonly AutoResetEvent _ackSignal = new AutoResetEvent(false);
        readonly ManualResetEvent _gotSettings = new ManualResetEvent(false);
        BitrateController _rate;
        AudioStreamer _audio;
        readonly object _audioGate = new object();
        bool _inputWasAllowed = true;

        public event Action<HostSession, string> Ended;
        public event Action<HostSession, bool> PrivacyRequested;
        public event Action<HostSession, string> ChatReceived;
        public Action<string> ClipboardFromViewer;
        public readonly FileTransfer Files;
        public volatile bool PrivacyActive;
        public string Remote => _conn.Remote;
        public string EncoderName { get; private set; } = "-";
        public int CurrentKbps => _rate?.Current ?? _kbps;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public HostSession(Connection conn)
        {
            _conn = conn;
            Files = new FileTransfer(conn);
        }

        public void Start()
        {
            _monitors = Monitors.Enumerate();
            _monitor = Math.Max(0, _monitors.FindIndex(m => m.Primary));
            _conn.OnMessage += OnMessage;
            _conn.OnClosed += reason =>
            {
                _stop = true; _ackSignal.Set();
                try { _input.ReleaseAll(); } catch { }
                lock (_audioGate) { _audio?.Dispose(); _audio = null; }
                Log.Info("Sesi selesai (" + Remote + "): " + reason);
                Ended?.Invoke(this, reason);
            };
            _conn.Start();
            SendHostInfo();
            new Thread(CaptureLoop) { IsBackground = true, Name = "capture", Priority = ThreadPriority.AboveNormal }.Start();
            Log.Info("Sesi dimulai dari " + Remote);
        }

        void SendHostInfo()
        {
            var mons = _monitors;
            byte features = HostFeatures.AllMonitors | HostFeatures.Files | HostFeatures.Chat | HostFeatures.Audio | HostFeatures.AutoBitrate;
            if (PrivacySupported) features |= HostFeatures.Privacy;
            _conn.Send(Msg.HostInfo, w =>
            {
                w.Write(Environment.MachineName);
                w.Write(mons.Count);
                foreach (var m in mons) w.Write(m.ToString());
                w.Write(_monitor);
                // ---- v2 extension (v1 viewers simply stop reading here)
                w.Write(2);
                w.Write(features);
                foreach (var m in mons) { w.Write(m.X); w.Write(m.Y); w.Write(m.Width); w.Write(m.Height); w.Write((byte)(m.Primary ? 1 : 0)); }
            });
        }

        public void Stop(string reason) => _conn.Bye(reason);
        public void SendClipboard(string text) => _conn.Send(Msg.Clipboard, Encoding.UTF8.GetBytes(text));
        public void SendChat(string text) => _conn.Send(Msg.Chat, Encoding.UTF8.GetBytes(text));

        void OnMessage(byte type, byte[] d)
        {
            if (Files.Handle(type, d)) return;
            bool input = AllowInput;
            if (!input && _inputWasAllowed) _input.ReleaseAll();
            _inputWasAllowed = input;
            switch (type)
            {
                case Msg.MouseMove: if (input && d.Length >= 4) _input.MouseMove(BitConverter.ToUInt16(d, 0), BitConverter.ToUInt16(d, 2)); break;
                case Msg.MouseButton: if (input && d.Length >= 2) _input.MouseButton(d[0], d[1] != 0); break;
                case Msg.MouseWheel: if (input && d.Length >= 3) _input.Wheel(BitConverter.ToInt16(d, 0), d[2] != 0); break;
                case Msg.Key: if (input && d.Length >= 5) _input.Key(BitConverter.ToUInt16(d, 0), BitConverter.ToUInt16(d, 2), d[4]); break;
                case Msg.ReleaseAll: _input.ReleaseAll(); break;
                case Msg.KeyframeReq: _keyRequested = true; break;
                case Msg.Clipboard: ClipboardFromViewer?.Invoke(Encoding.UTF8.GetString(d)); break;
                case Msg.Chat: ChatReceived?.Invoke(this, Encoding.UTF8.GetString(d)); break;
                case Msg.Privacy: if (d.Length > 0 && PrivacySupported) PrivacyRequested?.Invoke(this, d[0] != 0); break;
                case Msg.Ack:
                    if (d.Length >= 4)
                    {
                        int id = BitConverter.ToInt32(d, 0);
                        long now = Stopwatch.GetTimestamp();
                        double lat = Ms(now - Volatile.Read(ref _sentAt[id & 63]));
                        lock (_lat)
                        {
                            _lat.Enqueue(new KeyValuePair<long, double>(now, lat));
                            while (_lat.Count > 0 && Ms(now - _lat.Peek().Key) > 8000) _lat.Dequeue();
                            double min = double.MaxValue;
                            foreach (var s in _lat) if (s.Value < min) min = s.Value;
                            _baseLatency = min;
                        }
                        if (id > Volatile.Read(ref _lastAcked)) Volatile.Write(ref _lastAcked, id);
                        _ackSignal.Set();
                    }
                    break;
                case Msg.Settings:
                    using (var br = new BinaryReader(new MemoryStream(d)))
                    {
                        int kbps = br.ReadInt32(), fps = br.ReadInt32(), scale = br.ReadInt32(), mon = br.ReadInt32();
                        byte codec = br.ReadByte(); bool h264 = br.ReadByte() != 0;
                        bool auto = false, audio = false;
                        if (br.BaseStream.Position < br.BaseStream.Length) auto = br.ReadByte() != 0;   // v2
                        if (br.BaseStream.Position < br.BaseStream.Length) audio = br.ReadByte() != 0;  // v2
                        kbps = Clamp(kbps, 500, 100000); fps = Clamp(fps, 5, 144); scale = Clamp(scale, 25, 100);
                        int count = _monitors.Count;
                        if (mon == Monitors.All && count < 2) mon = 0;
                        if (mon != Monitors.All && (mon < 0 || mon >= count)) mon = _monitor;
                        bool full = fps != _fps || scale != _scale || mon != _monitor || codec != _codecPref || h264 != _viewerH264;
                        if (kbps != _kbps || auto != _autoBitrate) { _kbps = kbps; _autoBitrate = auto; _bitrateDirty = true; }
                        _fps = fps; _scale = scale; _monitor = mon; _codecPref = codec; _viewerH264 = h264;
                        if (full) _reconfigure = true;
                        SetAudio(audio);
                    }
                    _gotSettings.Set();
                    break;
            }
        }

        void SetAudio(bool on)
        {
            lock (_audioGate)
            {
                _audioWanted = on;
                if (on && _audio == null && !_stop) { try { _audio = new AudioStreamer(_conn); } catch (Exception ex) { Log.Warn("audio: " + ex.Message); } }
                else if (!on && _audio != null) { _audio.Dispose(); _audio = null; }
            }
        }

        void OnPacket(byte[] data, bool key)
        {
            int id = Interlocked.Increment(ref _lastSent);
            Volatile.Write(ref _sentAt[id & 63], Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _framesThisSecond);
            var p = new byte[5 + data.Length];
            BitConverter.GetBytes(id).CopyTo(p, 0);
            p[4] = (byte)(key ? 1 : 0);
            Buffer.BlockCopy(data, 0, p, 5, data.Length);
            _conn.Send(Msg.VideoFrame, p);
        }

        void CaptureLoop()
        {
            Native.EnsureMta();
            Native.timeBeginPeriod(1);
            IScreenCapture cap = null; IFrameEncoder enc = null; IntPtr scaled = IntPtr.Zero;
            int encW = 0, encH = 0, capW = 0, capH = 0, refreshes = 0;
            bool haveFrame = false;
            double lastFrame = -1000, lastChange = 0, lastCursorCheck = 0, lastStats = 0;
            byte lastCursor = 254;
            var clock = Stopwatch.StartNew();
            try
            {
                _gotSettings.WaitOne(800); // start directly with the viewer's preferred settings
                while (!_stop)
                {
                    if (_reconfigure || cap == null)
                    {
                        _reconfigure = false;
                        enc?.Dispose(); enc = null; cap?.Dispose(); cap = null;
                        if (scaled != IntPtr.Zero) { Marshal.FreeHGlobal(scaled); scaled = IntPtr.Zero; }

                        var fresh = Monitors.Enumerate(); // monitors may have been plugged in/out or rotated
                        if (!Monitors.SameLayout(fresh, _monitors))
                        {
                            _monitors = fresh;
                            if (_monitor != Monitors.All && _monitor >= fresh.Count) _monitor = Math.Max(0, fresh.FindIndex(m => m.Primary));
                            SendHostInfo();
                        }
                        bool all = _monitor == Monitors.All && _monitors.Count > 1;
                        MonitorInfo target;
                        if (all) { var mc = new MultiCapture(_monitors); cap = mc; target = mc.BoundsInfo; }
                        else { target = _monitors[Clamp(_monitor, 0, _monitors.Count - 1)]; cap = Monitors.Open(target); }
                        _input.SetMonitor(target);
                        capW = cap.Width; capH = cap.Height;
                        double s = _scale / 100.0;
                        if (all) s = Math.Min(s, Math.Min(1.0, Math.Min(Math.Sqrt(5.0e6 / ((double)capW * capH)), Math.Min(3840.0 / capW, 3840.0 / capH))));
                        encW = ((int)(capW * s)) & ~1; encH = ((int)(capH * s)) & ~1;
                        if (s < 0.999) scaled = Marshal.AllocHGlobal(encW * encH * 4);
                        byte codec = _codecPref == CodecId.H264 && _viewerH264 ? CodecId.H264 : CodecId.Jpeg;
                        int startKbps = _autoBitrate ? Math.Min(_rate?.Current ?? 6000, 20000) : _kbps;
                        _rate = _autoBitrate ? new BitrateController(startKbps, Math.Max(_kbps, 20000)) : null;
                        enc = Codecs.CreateEncoder(codec, encW, encH, _fps, startKbps, OnPacket);
                        _bitrateDirty = false;
                        EncoderName = enc.Name;
                        Volatile.Write(ref _lastAcked, Math.Max(Volatile.Read(ref _lastAcked), Volatile.Read(ref _lastSent)));
                        string capKind = cap.Kind; int monIdx = all ? Monitors.All : Clamp(_monitor, 0, _monitors.Count - 1);
                        _conn.Send(Msg.VideoFormat, w =>
                        {
                            w.Write(enc.Codec); w.Write(encW); w.Write(encH); w.Write(monIdx);
                            w.Write(enc.Name); w.Write(capKind);
                        });
                        Log.Info("Stream " + target + " -> " + encW + "x" + encH + "@" + _fps + " " + (_autoBitrate ? "auto " : "") + startKbps + "kbps, encoder=" + enc.Name + ", capture=" + capKind);
                        haveFrame = false; refreshes = 0;
                    }
                    if (_bitrateDirty)
                    {
                        _bitrateDirty = false;
                        if (_autoBitrate) { if (_rate == null) _rate = new BitrateController(Math.Min(_kbps, 6000), Math.Max(_kbps, 20000)); enc.SetBitrate(_rate.Current); }
                        else { _rate = null; enc.SetBitrate(_kbps); }
                    }

                    double now0 = clock.Elapsed.TotalMilliseconds;
                    if (now0 - lastStats >= 1000)
                    {
                        lastStats = now0;
                        int frames = Interlocked.Exchange(ref _framesThisSecond, 0);
                        if (_rate != null && _rate.Tick(frames >= 5)) enc.SetBitrate(_rate.Current);
                        int kb = _rate?.Current ?? _kbps;
                        byte flags = (byte)((AllowInput ? 1 : 0) | (PrivacyActive ? 2 : 0) | (_audioWanted ? 4 : 0));
                        _conn.Send(Msg.HostStats, w => { w.Write(kb); w.Write((byte)(_autoBitrate ? 1 : 0)); w.Write(flags); w.Write(frames); });
                    }

                    // Delay-based flow control: if the oldest unacknowledged frame is older than the best delivery
                    // time seen (+ slack), frames are queuing (network or slow viewer): skip capturing until it drains.
                    double interval = 1000.0 / _fps;
                    int sent = Volatile.Read(ref _lastSent), acked = Volatile.Read(ref _lastAcked);
                    if (sent - acked > 0)
                    {
                        double oldest = Ms(Stopwatch.GetTimestamp() - Volatile.Read(ref _sentAt[(acked + 1) & 63]));
                        double allowed = (_baseLatency > 0 ? _baseLatency : 150) + Math.Max(1.5 * interval, 12);
                        if (sent - acked >= 10 || oldest > allowed || _conn.QueuedBytes > 8 * 1024 * 1024)
                        {
                            _rate?.NoteCongestion();
                            _ackSignal.WaitOne(4);
                            continue;
                        }
                    }

                    // pacing: at most _fps frames per second (changes accumulate meanwhile)
                    double now = clock.Elapsed.TotalMilliseconds;
                    double wait = lastFrame + interval - now;
                    if (wait >= 1) Thread.Sleep((int)wait);

                    var r = cap.Capture(haveFrame ? 20 : 100);
                    now = clock.Elapsed.TotalMilliseconds;
                    if (r == CaptureResult.Error) { Log.Warn("capture error, reopening"); _reconfigure = true; Thread.Sleep(200); continue; }
                    if (cap.Width != capW || cap.Height != capH) { _reconfigure = true; continue; } // resolution/rotation changed

                    bool key = _keyRequested;
                    bool encode = false;
                    if (r == CaptureResult.Changed) { haveFrame = true; refreshes = 0; lastChange = now; encode = true; }
                    else if (haveFrame && (key || (enc.Codec == CodecId.H264 && refreshes < 3 && now - lastChange > 120 + refreshes * 120)))
                    { refreshes++; lastChange = now; encode = true; }

                    if (encode)
                    {
                        if (key) _keyRequested = false;
                        byte* src = (byte*)cap.Pixels; int stride = cap.Stride;
                        if (scaled != IntPtr.Zero)
                        {
                            ColorConvert.ScaleBgra(src, capW, capH, stride, (byte*)scaled, encW, encH, encW * 4);
                            src = (byte*)scaled; stride = encW * 4;
                        }
                        enc.Encode(src, stride, clock.Elapsed.Ticks, key);
                        lastFrame = now;
                    }

                    if (now - lastCursorCheck > 30)
                    {
                        lastCursorCheck = now;
                        byte c = CursorShapes.Current();
                        if (c != lastCursor) { lastCursor = c; _conn.Send(Msg.Cursor, new[] { c }); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("capture loop", ex);
                _conn.Bye("Error di komputer host: " + ex.Message);
            }
            finally
            {
                try { enc?.Dispose(); } catch { }
                try { cap?.Dispose(); } catch { }
                if (scaled != IntPtr.Zero) Marshal.FreeHGlobal(scaled);
                Native.timeEndPeriod(1);
            }
        }
    }

    /// <summary>Accepts viewers on a TCP port (normal mode) or dials out to a waiting viewer (reverse mode).</summary>
    internal sealed class HostService
    {
        TcpListener _listener;
        ListenerSecret _secret;
        volatile HostSession _session;
        int _busy, _pending;
        public event Action<string> Status;
        public event Action<HostSession> SessionStarted;
        public event Action<HostSession, string> SessionEnded;
        /// <summary>Optional: asked (on a worker thread) before a new viewer is let in. Return true to allow.</summary>
        public Func<string, bool> ApproveConnection;
        public bool IsListening => _listener != null;
        public HostSession Session => _session;
        public int Port { get; private set; }

        /// <summary>
        /// Windows lets us bind [::]:port even when another app (e.g. AnyDesk on 7070) holds 0.0.0.0:port —
        /// then all IPv4 traffic silently goes to that other app. So refuse ports that are already listening.
        /// </summary>
        public static void EnsurePortFree(int port)
        {
            foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port)
                    throw new InvalidOperationException("Port " + port + " sudah dipakai aplikasi lain (contoh: AnyDesk memakai 7070). " +
                                                        "Pakai port lain, misalnya 7171, lalu samakan port forwarding di router.");
        }

        /// <summary>Set by the test modes: listen on 127.0.0.1 only (no firewall prompt).</summary>
        public static bool TestLoopbackOnly;

        public void StartListening(int port, string password, bool loopbackOnly = false)
        {
            loopbackOnly |= TestLoopbackOnly;
            StopListening();
            EnsurePortFree(port);
            _secret = new ListenerSecret(password);
            var l = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.IPv6Any, port);
            if (!loopbackOnly) l.Server.DualMode = true;
            l.Start();
            _listener = l; Port = port;
            new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "host accept" }.Start();
            Log.Info("Host mendengarkan di port " + port);
        }

        public void StopListening()
        {
            var l = _listener; _listener = null;
            try { l?.Stop(); } catch { }
        }

        byte Admit(string remote)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return Secure.R_BUSY;
            var approve = ApproveConnection;
            if (approve != null)
            {
                bool ok;
                try { ok = approve(remote); } catch { ok = false; }
                if (!ok) { Interlocked.Exchange(ref _busy, 0); return Secure.R_DENIED; }
            }
            return Secure.R_OK;
        }

        void AcceptLoop(TcpListener l)
        {
            while (_listener == l)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { break; }
                if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); c.Close(); continue; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Begin(Secure.Accept(c, AppRole.Host, _secret, Admit)); }
                    catch (AuthException ex) { Log.Warn("Login ditolak: " + ex.Message); Status?.Invoke("Percobaan masuk ditolak: " + ex.Message); }
                    catch (Exception ex) { Log.Warn("Handshake gagal: " + ex.Message); }
                    finally { Interlocked.Decrement(ref _pending); }
                });
            }
        }

        /// <summary>Reverse mode: this PC (host) connects to a viewer that is waiting for us.</summary>
        public void ConnectOut(string host, int port, string password, Func<string, string, bool> confirmChanged)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Masih ada sesi yang aktif.");
            try { Begin(Secure.Dial(host, port, AppRole.Host, password, confirmChanged)); }
            catch { Interlocked.Exchange(ref _busy, 0); throw; }
        }

        void Begin(Connection conn)
        {
            var s = new HostSession(conn);
            s.Ended += (x, reason) =>
            {
                _session = null;
                Interlocked.Exchange(ref _busy, 0);
                SessionEnded?.Invoke(x, reason);
            };
            _session = s;
            SessionStarted?.Invoke(s);
            s.Start();
        }

        public void Disconnect(string reason = "Diputus oleh pemilik komputer") => _session?.Stop(reason);
    }
}
