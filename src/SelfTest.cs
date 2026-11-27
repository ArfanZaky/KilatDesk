using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KilatDesk
{
    /// <summary>Headless checks: KilatDesk.exe --selftest [report.txt]</summary>
    internal static unsafe class SelfTest
    {
        static StringBuilder _r;
        static void P(string s) { _r.AppendLine(s); }

        public static int Run(string reportPath)
        {
            _r = new StringBuilder();
            int failures = 0;
            Native.timeBeginPeriod(1);
            try
            {
                P("KilatDesk self-test " + DateTime.Now);
                var mons = Monitors.Enumerate();
                foreach (var m in mons) P("Monitor: " + m + " at " + m.X + "," + m.Y + " dxgi=" + m.Adapter + "/" + m.Output);
                var mon = mons.Find(x => x.Primary) ?? mons[0];
                failures += TestMonitors(mons, Path.GetDirectoryName(Path.GetFullPath(reportPath)));

                // ---- capture
                IntPtr frame; int w, h;
                using (var cap = Monitors.Open(mon))
                {
                    var sw = Stopwatch.StartNew();
                    int changed = 0, n = 0;
                    while (sw.ElapsedMilliseconds < 1000) { n++; if (cap.Capture(16) == CaptureResult.Changed) changed++; }
                    P("Capture " + cap.Kind + " " + cap.Width + "x" + cap.Height + ": " + n + " calls/s, changed=" + changed);
                    if (changed == 0) { P("FAIL: no frame captured"); failures++; }
                    w = cap.Width & ~1; h = cap.Height & ~1;
                    frame = Marshal.AllocHGlobal(cap.Stride * cap.Height);
                    Buffer.MemoryCopy((void*)cap.Pixels, (void*)frame, cap.Stride * cap.Height, cap.Stride * cap.Height);
                }
                int stride = (mon.Width) * 4;

                // ---- synthetic motion: scroll the captured picture vertically
                const int Frames = 90;
                var src = new List<IntPtr>();
                for (int i = 0; i < Frames; i++)
                {
                    IntPtr f = Marshal.AllocHGlobal(stride * h);
                    int shift = (i * 7) % h;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)frame + (long)((y + shift) % h) * stride, (byte*)f + (long)y * stride, stride, stride);
                    src.Add(f);
                }

                // ---- NV12 conversion speed
                IntPtr nv12 = Marshal.AllocHGlobal(w * h * 3 / 2);
                var t = Stopwatch.StartNew();
                for (int i = 0; i < 30; i++) ColorConvert.BgraToNv12((byte*)src[i], stride, w, h, (byte*)nv12, w);
                P("BGRA->NV12: " + (t.Elapsed.TotalMilliseconds / 30).ToString("0.00") + " ms/frame");
                IntPtr back = Marshal.AllocHGlobal(w * h * 4);
                t.Restart();
                for (int i = 0; i < 30; i++) ColorConvert.Nv12ToBgra((byte*)nv12, w, (byte*)nv12 + w * h, w, w, h, (byte*)back, w * 4);
                P("NV12->BGRA: " + (t.Elapsed.TotalMilliseconds / 30).ToString("0.00") + " ms/frame");
                {
                    int sw2 = w * 3 / 4 & ~1, sh2 = h * 3 / 4 & ~1;
                    IntPtr scaledBuf = Marshal.AllocHGlobal(sw2 * sh2 * 4);
                    ColorConvert.ScaleBgra((byte*)src[0], w, h, stride, (byte*)scaledBuf, sw2, sh2, sw2 * 4);
                    t.Restart();
                    for (int i = 0; i < 30; i++) ColorConvert.ScaleBgra((byte*)src[i], w, h, stride, (byte*)scaledBuf, sw2, sh2, sw2 * 4);
                    P("Scale " + w + "x" + h + " -> " + sw2 + "x" + sh2 + " (viewer window): " + (t.Elapsed.TotalMilliseconds / 30).ToString("0.00") + " ms/frame");
                    Marshal.FreeHGlobal(scaledBuf);
                }

                failures += TestCodec("H.264 GPU", CodecId.H264, true, w, h, stride, src, back);
                failures += TestCodec("H.264 CPU", CodecId.H264, false, w, h, stride, src, back);
                failures += TestCodec("JPEG", CodecId.Jpeg, false, w, h, stride, src, back);

                foreach (var f in src) Marshal.FreeHGlobal(f);
                Marshal.FreeHGlobal(frame); Marshal.FreeHGlobal(nv12); Marshal.FreeHGlobal(back);

                failures += TestAudioAndControl();
                failures += NetTest.Run(P);
                failures += NetTest.EndToEnd(P);
            }
            catch (Exception ex) { P("FAIL: " + ex); failures++; }
            finally { Native.timeEndPeriod(1); }
            P(failures == 0 ? "RESULT: ALL OK" : "RESULT: " + failures + " FAILURE(S)");
            File.WriteAllText(reportPath, _r.ToString());
            return failures;
        }

        static int TestCodec(string label, byte codec, bool hw, int w, int h, int stride, List<IntPtr> src, IntPtr fb)
        {
            var packets = new List<byte[]>();
            int keys = 0, lastAccepted = 0;
            var clock = Stopwatch.StartNew();
            var inTimes = new Queue<double>();
            double latSum = 0; int latN = 0;
            Action<byte[], bool> onPacket = (p, k) =>
            {
                lock (packets)
                {
                    packets.Add(p); if (k) keys++;
                    if (inTimes.Count > 0) { latSum += clock.Elapsed.TotalMilliseconds - inTimes.Dequeue(); latN++; }
                }
            };
            IFrameEncoder enc = null; H264Encoder raw = null; IntPtr nv12 = IntPtr.Zero;
            try
            {
                string name;
                if (codec == CodecId.H264)
                {
                    raw = H264Encoder.Create(w, h, 60, 8000, hw, onPacket);
                    name = raw.Name;
                    if (hw && !raw.IsHardware) { P(label + ": no GPU encoder on this PC (skipped)"); return 0; }
                    nv12 = Marshal.AllocHGlobal(w * h * 3 / 2);
                }
                else { enc = new JpegFrameEncoder(w, h, 8000, onPacket); name = enc.Name; }

                var t = Stopwatch.StartNew();
                int dropped = 0;
                for (int i = 0; i < src.Count; i++)
                {
                    long ts = i * 166_666L;
                    lock (packets) inTimes.Enqueue(clock.Elapsed.TotalMilliseconds);
                    bool ok;
                    if (raw != null)
                    {
                        ColorConvert.BgraToNv12((byte*)src[i], stride, w, h, (byte*)nv12, w);
                        ok = raw.Encode((byte*)nv12, ts, i == 0);
                    }
                    else ok = enc.Encode((byte*)src[i], stride, ts, i == 0);
                    if (ok) lastAccepted = i;
                    else { dropped++; lock (packets) { var q = new Queue<double>(inTimes); inTimes.Clear(); var arr = q.ToArray(); for (int k = 0; k < arr.Length - 1; k++) inTimes.Enqueue(arr[k]); } }
                }
                double encMs = t.Elapsed.TotalMilliseconds / src.Count;
                Thread.Sleep(300); // let async GPU encoders flush
                long bytes = 0; lock (packets) foreach (var p in packets) bytes += p.Length;
                P(label + " [" + name + "]: " + encMs.ToString("0.00") + " ms/frame, packets=" + packets.Count + " keys=" + keys +
                  " dropped=" + dropped + ", avg " + (bytes / Math.Max(1, packets.Count) / 1024.0).ToString("0.0") + " KB/frame (" +
                  (bytes * 8.0 * 60 / Math.Max(1, packets.Count) / 1e6).ToString("0.0") + " Mbps @60fps), encode latency " +
                  (latSum / Math.Max(1, latN)).ToString("0.0") + " ms");

                // decode everything and compare the last picture with its source
                using (var dec = Codecs.CreateDecoder(codec, w, h))
                {
                    t.Restart();
                    int decoded = 0;
                    foreach (var p in packets) if (dec.Decode(p, 0, p.Length, (byte*)fb, w * 4)) decoded++;
                    double decMs = t.Elapsed.TotalMilliseconds / Math.Max(1, packets.Count);
                    double psnr = Psnr((byte*)src[lastAccepted], stride, (byte*)fb, w * 4, w, h);
                    P("   decode: " + decMs.ToString("0.00") + " ms/frame, pictures=" + decoded + ", PSNR(last)=" + psnr.ToString("0.0") + " dB");
                    if (decoded == 0 || psnr < 25) { P("   FAIL: decode/quality check"); return 1; }
                }
                return 0;
            }
            catch (Exception ex) { P(label + " FAIL: " + ex.Message); return 1; }
            finally
            {
                enc?.Dispose(); raw?.Dispose();
                if (nv12 != IntPtr.Zero) Marshal.FreeHGlobal(nv12);
            }
        }

        /// <summary>Every monitor: DXGI (incl. rotation fix) must match GDI; all-monitor composite must work.</summary>
        static int TestMonitors(List<MonitorInfo> mons, string outDir)
        {
            int fails = 0;
            foreach (var m in mons)
            {
                try
                {
                    using (var gdi = new GdiCapture(m))
                    using (var dx = new DxgiCapture(m))
                    {
                        var sw = Stopwatch.StartNew();
                        var r1 = CaptureResult.NoChange; int calls = 0;
                        while (r1 != CaptureResult.Changed && sw.ElapsedMilliseconds < 1500) { calls++; r1 = dx.Capture(100); }
                        double dxMs = sw.Elapsed.TotalMilliseconds;
                        gdi.Capture(0);
                        string info = m + ": DXGI " + dx.Width + "x" + dx.Height + " rot=" + dx.Rotation + " first picture=" + r1 + " after " + calls + " call(s), " + dxMs.ToString("0") + " ms";
                        if (r1 != CaptureResult.Changed) { P(info + "  FAIL: no picture"); fails++; continue; }
                        if (dx.Width != m.Width || dx.Height != m.Height) { P(info + "  FAIL: size differs from desktop"); fails++; continue; }
                        // (GDI shows DirectX/game windows as black, so it is only informative, not a pass/fail reference)
                        double psnr = Psnr((byte*)dx.Pixels, dx.Stride, (byte*)gdi.Pixels, gdi.Stride, m.Width & ~1, m.Height & ~1);
                        long lit = 0; uint* px = (uint*)dx.Pixels;
                        for (long i = 0, n = (long)dx.Width * dx.Height; i < n; i += 101) if ((px[i] & 0xFFFFFF) != 0) lit++;
                        P(info + " (present=" + dx.LastPresent + " acc=" + dx.LastAccumulated + "), vs GDI " + psnr.ToString("0.0") + " dB, non-black samples=" + lit);
                        if (lit == 0) { P("   FAIL: first DXGI picture is black"); fails++; }
                        if (dx.Rotation > 1) SavePng(dx.Pixels, dx.Width, dx.Height, Path.Combine(outDir, "rotated-monitor.png"));
                    }
                }
                catch (Exception ex) { P(m + ": FAIL " + ex.Message); fails++; }
            }
            if (mons.Count > 1)
            {
                try
                {
                    using (var mc = new MultiCapture(mons))
                    {
                        var sw = Stopwatch.StartNew();
                        var r = mc.Capture(300);
                        P("All monitors: " + mc.Width + "x" + mc.Height + " " + r + " in " + sw.ElapsedMilliseconds + " ms (" + mc.Kind + ")");
                        if (r != CaptureResult.Changed) { P("   FAIL: no composite frame"); fails++; }
                        SavePng(mc.Pixels, mc.Width, mc.Height, Path.Combine(outDir, "all-monitors.png"));
                    }
                }
                catch (Exception ex) { P("All monitors FAIL: " + ex.Message); fails++; }
            }
            return fails;
        }

        static int TestAudioAndControl()
        {
            int fails = 0;
            const int rate = 48000, frames = 960 * 10;
            var pcm = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                pcm[i * 2] = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 12000);
                pcm[i * 2 + 1] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            }
            var st = new Adpcm.State[2];
            double sig = 0, err = 0; int bytes = 0;
            for (int off = 0; off < frames; off += 960)
            {
                var chunk = new short[960 * 2];
                Array.Copy(pcm, off * 2, chunk, 0, chunk.Length);
                var enc = Adpcm.Encode(chunk, 960, 2, st);
                bytes += enc.Length;
                var dec = Adpcm.Decode(enc, 0, out _, out _);
                for (int k = 0; k < dec.Length; k++) { double d = chunk[k], e = d - dec[k]; sig += d * d; err += e * e; }
            }
            double snr = 10 * Math.Log10(sig / Math.Max(1, err));
            P("ADPCM audio codec: SNR " + snr.ToString("0.0") + " dB, " + (bytes * 8.0 / (frames / (double)rate) / 1000).ToString("0") + " kbps");
            if (snr < 20) { P("   FAIL: ADPCM quality"); fails++; }

            using (var cap = new AudioCapture())
            {
                int fmtRate = 0, packets = 0;
                cap.FormatReady += (r, c) => fmtRate = r;
                cap.Samples += (s, n) => packets++;
                cap.Start();
                Thread.Sleep(1200);
                P("Audio capture (WASAPI loopback): " + (fmtRate > 0 ? fmtRate + " Hz, packets=" + packets + (packets == 0 ? " (tidak ada suara diputar)" : "") : "tidak tersedia: " + cap.Error));
            }
            try { using (var pl = new AudioPlayer(48000, 2)) { pl.Feed(new short[48000 / 5 * 2]); Thread.Sleep(300); } P("Audio player (waveOut, silence): OK"); }
            catch (Exception ex) { P("Audio player WARN: " + ex.Message); }

            var bc = new BitrateController(8000, 20000);
            for (int i = 0; i < 30; i++) bc.NoteCongestion();
            bc.Tick(true);
            int down = bc.Current;
            for (int i = 0; i < 6; i++) bc.Tick(true);
            int up = bc.Current;
            P("Auto bitrate: 8000 -> congested " + down + " -> calm " + up + " kbps");
            if (!(down < 8000 && up > down)) { P("   FAIL: bitrate controller"); fails++; }
            return fails;
        }

        static void SavePng(IntPtr px, int w, int h, string path)
        {
            int tw = Math.Max(2, w / 4), th = Math.Max(2, h / 4);
            IntPtr small = Marshal.AllocHGlobal(tw * th * 4);
            try
            {
                ColorConvert.ScaleBgra((byte*)px, w, h, w * 4, (byte*)small, tw, th, tw * 4);
                using (var bmp = new System.Drawing.Bitmap(tw, th, tw * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, small)) bmp.Save(path);
            }
            finally { Marshal.FreeHGlobal(small); }
        }

        static double Psnr(byte* a, int sa, byte* b, int sb, int w, int h)
        {
            double se = 0; long n = 0;
            for (int y = 0; y < h; y += 3)
                for (int x = 0; x < w; x += 3)
                    for (int c = 0; c < 3; c++)
                    {
                        int d = a[(long)y * sa + x * 4 + c] - b[(long)y * sb + x * 4 + c];
                        se += d * d; n++;
                    }
            double mse = se / Math.Max(1, n);
            return mse < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
        }
    }
}
