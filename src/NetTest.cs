using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KilatDesk
{
    /// <summary>Loopback tests for TLS + password authentication + message transport + file transfer + full sessions.</summary>
    internal static class NetTest
    {
        public static int Run(Action<string> P)
        {
            int fails = 0;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var secret = new ListenerSecret("Rahasia-123");
            try
            {
                // 1) correct password + message round trip + throughput
                Connection server = null; Exception serr = null;
                var t = new Thread(() => { try { server = Secure.Accept(listener.AcceptTcpClient(), AppRole.Host, secret, r => Secure.R_OK); } catch (Exception ex) { serr = ex; } });
                t.Start();
                var sw = Stopwatch.StartNew();
                var client = Secure.Dial("127.0.0.1", port, AppRole.Viewer, "Rahasia-123", (a, b) => true);
                t.Join();
                if (server == null) throw new Exception("server side failed: " + serr);
                P("Auth OK (TLS + PBKDF2) in " + sw.ElapsedMilliseconds + " ms");

                var got = new ManualResetEvent(false);
                string text = null; long bulk = 0; var bulkDone = new ManualResetEvent(false);
                const int BulkMsgs = 200, BulkSize = 64 * 1024;
                var ftS = new FileTransfer(server);
                var ftC = new FileTransfer(client);
                server.OnMessage += (type, data) => { if (ftS.Handle(type, data)) return; if (type == Msg.Chat) { text = Encoding.UTF8.GetString(data); got.Set(); } };
                client.OnMessage += (type, data) =>
                {
                    if (ftC.Handle(type, data)) return;
                    if (type == Msg.VideoFrame && Interlocked.Add(ref bulk, data.Length) >= (long)BulkMsgs * BulkSize) bulkDone.Set();
                };
                server.Start(); client.Start();
                client.Send(Msg.Chat, Encoding.UTF8.GetBytes("halo dari viewer"));
                if (!got.WaitOne(3000) || text != "halo dari viewer") { P("FAIL: message round trip"); fails++; }
                else P("Message round trip (chat) OK");

                sw.Restart();
                var blob = new byte[BulkSize];
                for (int i = 0; i < BulkMsgs; i++) server.Send(Msg.VideoFrame, blob);
                if (!bulkDone.WaitOne(10000)) { P("FAIL: bulk transfer"); fails++; }
                else P("TLS throughput (loopback): " + (BulkMsgs * BulkSize / 1048576.0 / sw.Elapsed.TotalSeconds).ToString("0") + " MB/s");

                fails += FileRoundTrip(P, ftC, ftS, "viewer->host");
                fails += FileRoundTrip(P, ftS, ftC, "host->viewer");

                client.Bye("test selesai");
                Thread.Sleep(200);
                if (!server.IsClosed) { P("FAIL: Bye not received"); fails++; } else P("Bye OK: '" + server.CloseReason + "'");

                // 2) wrong password must be rejected on both sides
                byte serverCode = 0;
                t = new Thread(() => { try { Secure.Accept(listener.AcceptTcpClient(), AppRole.Host, secret, r => Secure.R_OK); } catch (AuthException ex) { serverCode = ex.Code; } catch { } });
                t.Start();
                try { Secure.Dial("127.0.0.1", port, AppRole.Viewer, "salah", null); P("FAIL: wrong password accepted"); fails++; }
                catch (AuthException ex) { P("Wrong password rejected: " + ex.Message); if (ex.Code != Secure.R_BADPASS) fails++; }
                t.Join();
                if (serverCode != Secure.R_BADPASS) { P("FAIL: server did not flag bad password"); fails++; }

                // 3) both sides in host mode must be refused
                t = new Thread(() => { try { Secure.Accept(listener.AcceptTcpClient(), AppRole.Host, secret, r => Secure.R_OK); } catch { } });
                t.Start();
                try { Secure.Dial("127.0.0.1", port, AppRole.Host, "Rahasia-123", null); P("FAIL: same-role accepted"); fails++; }
                catch (AuthException ex) { P("Same role rejected: " + ex.Message); }
                t.Join();

                // 4) host owner rejects the connection (approval prompt answered "Tolak")
                t = new Thread(() => { try { Secure.Accept(listener.AcceptTcpClient(), AppRole.Host, secret, r => Secure.R_DENIED); } catch { } });
                t.Start();
                try { Secure.Dial("127.0.0.1", port, AppRole.Viewer, "Rahasia-123", null); P("FAIL: denied connection accepted"); fails++; }
                catch (AuthException ex) { P("Owner denial reported: " + ex.Message); if (ex.Code != Secure.R_DENIED) fails++; }
                t.Join();
            }
            catch (Exception ex) { P("FAIL net: " + ex); fails++; }
            finally { listener.Stop(); }
            return fails;
        }

        static int FileRoundTrip(Action<string> P, FileTransfer from, FileTransfer to, string label)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "kd-ft-" + Guid.NewGuid().ToString("N"));
            string prevDir = FileTransfer.DownloadDirOverride;
            try
            {
                Directory.CreateDirectory(tmp);
                FileTransfer.DownloadDirOverride = Path.Combine(tmp, "masuk");
                string src = Path.Combine(tmp, "data uji " + label.Replace(">", "") + ".bin");
                var data = new byte[3 * 1024 * 1024 + 4321];
                new Random(7).NextBytes(data);
                File.WriteAllBytes(src, data);
                string saved = null; var done = new ManualResetEvent(false);
                Action<string> onRecv = p => { saved = p; done.Set(); };
                to.Received += onRecv;
                var sw = Stopwatch.StartNew();
                from.SendFiles(new[] { src });
                bool ok = done.WaitOne(20000) && File.Exists(saved) && File.ReadAllBytes(saved).SequenceEqual(data);
                to.Received -= onRecv;
                P(ok ? "File " + label + " OK: 3 MB identik, " + (data.Length / 1048576.0 / sw.Elapsed.TotalSeconds).ToString("0") + " MB/s -> " + Path.GetFileName(saved)
                     : "FAIL: file " + label + " (saved=" + saved + ")");
                return ok ? 0 : 1;
            }
            catch (Exception ex) { P("FAIL: file " + label + ": " + ex.Message); return 1; }
            finally
            {
                FileTransfer.DownloadDirOverride = prevDir;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static bool WaitUntil(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
            return cond();
        }

        static unsafe long Lit(ViewerSession vs)
        {
            long lit = 0;
            lock (vs.FrameLock)
                if (vs.Frame != IntPtr.Zero)
                {
                    uint* px = (uint*)vs.Frame;
                    for (long i = 0, n = (long)vs.Width * vs.Height; i < n; i += 97) if ((px[i] & 0xFFFFFF) != 0) lit++;
                }
            return lit;
        }

        /// <summary>Full pipeline on loopback: HostService (capture+encode) -> ViewerSession (decode), incl. monitor switching.</summary>
        public static int EndToEnd(Action<string> P)
        {
            int fails = 0;
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var host = new HostService();
            try
            {
                host.StartListening(port, "Uji-Coba-99", loopbackOnly: true);

                // ---- H.264 session: features + switch through every monitor + all monitors
                {
                    var vs = new ViewerSession(Secure.Dial("127.0.0.1", port, AppRole.Viewer, "Uji-Coba-99", null))
                    { CodecPref = CodecId.H264, Kbps = 8000, Fps = 60, Scale = 100, AutoBitrate = true, Audio = true, MonitorRequest = 0 };
                    string closed = null;
                    vs.Closed += r => closed = r;
                    vs.Start();
                    WaitUntil(() => vs.FramesTotal > 0 && vs.HasHostStats, 5000);
                    Thread.Sleep(1500);
                    vs.UpdateStats();
                    P("E2E fitur: features=" + vs.Features + " monitors=" + vs.MonitorList.Count + "/" + vs.MonitorNames.Length + " hostStats=" + vs.HasHostStats +
                      " auto=" + vs.HostAuto + " (" + vs.HostKbps + " kbps) audio=" + (vs.AudioRate > 0 ? vs.AudioRate + " Hz, " + vs.AudioPackets + " paket" : "belum ada format"));
                    if (vs.Features == 0 || vs.MonitorList.Count != vs.MonitorNames.Length || !vs.HasHostStats || !vs.HostAuto) { P("   FAIL: extended handshake data"); fails++; }

                    int count = vs.MonitorNames.Length;
                    var order = Enumerable.Range(0, count).ToList();
                    if (count > 1) order.Add(Monitors.All);
                    foreach (int m in order)
                    {
                        if (m != 0 || vs.Monitor != 0) vs.RequestMonitor(m);
                        bool switched = WaitUntil(() => vs.Monitor == m && vs.FramesTotal > 0, 5000);
                        int before = vs.FramesTotal;
                        Thread.Sleep(1200);
                        long lit = Lit(vs);
                        string name = m == Monitors.All ? "Semua monitor" : vs.MonitorNames[m];
                        P("E2E " + name + ": " + vs.Width + "x" + vs.Height + " cap=" + vs.CaptureKind + " enc=" + vs.EncoderName +
                          " frames+" + (vs.FramesTotal - before) + " non-black=" + lit + (switched ? "" : "  (NOT SWITCHED)"));
                        if (!switched || lit == 0) { P("   FAIL: monitor " + name); fails++; }
                        if (m != Monitors.All && m < vs.MonitorList.Count)
                        {
                            var mi = vs.MonitorList[m];
                            if (Math.Abs(vs.Width - (mi.Width & ~1)) > 1 || Math.Abs(vs.Height - (mi.Height & ~1)) > 1) { P("   FAIL: size does not match monitor " + mi.Width + "x" + mi.Height); fails++; }
                        }
                    }
                    if (closed != null) { P("   FAIL: session closed: " + closed); fails++; }
                    vs.Dispose();
                    Thread.Sleep(300);
                }

                // ---- JPEG mode at 50 %
                {
                    var vs = new ViewerSession(Secure.Dial("127.0.0.1", port, AppRole.Viewer, "Uji-Coba-99", null))
                    { CodecPref = CodecId.Jpeg, Kbps = 8000, Fps = 60, Scale = 50, AutoBitrate = false, Audio = false, MonitorRequest = 0 };
                    vs.Start();
                    WaitUntil(() => vs.FramesTotal > 0, 5000);
                    Thread.Sleep(2000); vs.UpdateStats();
                    long lit = Lit(vs);
                    P("E2E JPEG: " + vs.Width + "x" + vs.Height + " enc=" + vs.EncoderName + " frames=" + vs.FramesTotal + " rtt=" + vs.Rtt.ToString("0.0") + "ms non-black=" + lit);
                    if (vs.FramesTotal < 1 || lit == 0 || vs.Codec != CodecId.Jpeg) { P("   FAIL: JPEG picture"); fails++; }
                    vs.Dispose();
                    Thread.Sleep(300);
                }
            }
            catch (Exception ex) { P("FAIL e2e: " + ex); fails++; }
            finally { host.StopListening(); }
            return fails;
        }
    }
}
