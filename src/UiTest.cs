using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KilatDesk
{
    internal sealed partial class MainForm
    {
        internal void TestSelectTab(int i) => ((TabControl)Controls[0]).SelectedIndex = i;
        internal string TestHostPassword => _hpass.Text;
        internal void TestSetPorts(int host, int viewer) { _hport.Value = host; _vport.Value = viewer; }
        internal void TestStartHost() => StartHost(true);
        internal string TestHostStatus => _hstatus.Text;
        internal Task TestConnect(string addr, string pw) { _addr.Text = addr; _vpass.Text = pw; _remember.Checked = false; return ConnectAsync(); }
        internal void TestStartViewerListen(string pw) { _vpass.Text = pw; ToggleViewerListen(); }
        internal Task TestReverse(string addr) { _reverseAddr.Text = addr; return ReverseConnectAsync(); }
        internal string TestViewerStatus => _vstatus.Text;
        internal void TestStopAll() { if (_vlisten.IsListening) ToggleViewerListen(); StopHost(); }
        internal void TestExit() => ExitApp();
        internal HostSession TestSession => _host.Session;
        internal string TestHostChat => _hostChat != null && _hostChat.Visible ? _hostChat.TestLog : null;
        internal bool TestPrivacyOk => _privacy.Active && _privacy.VerifyExcluded();
        internal void TestSetAllowInput(bool v) => _allowInput.Checked = v;
        internal void TestSetAskApproval(bool v) => _askApproval.Checked = v;
    }

    internal sealed partial class ViewerForm
    {
        internal string TestInfo => _hostLabel.Text + " | " + _stats.Text;
        internal ViewerSession TestSession => _s;
        internal string[] TestMonitorButtons => _monButtons.Select(b => b.Text + (b.Checked ? "[x]" : "")).ToArray();
        internal void TestClickMonitor(int i) => _monButtons[i].PerformClick();
        internal string TestScreenshot() { TakeScreenshot(); return _statusPath; }
        internal string TestChat => _chat != null ? _chat.TestLog : null;
    }

    /// <summary>KilatDesk.exe --uitest &lt;outDir&gt; : clicks through the real windows on this PC (loopback) and saves screenshots.</summary>
    internal static class UiTest
    {
        static readonly StringBuilder R = new StringBuilder();
        static int _fails;

        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        static void Check(bool ok, string what) { R.AppendLine((ok ? "OK    " : "FAIL  ") + what); if (!ok) _fails++; }

        static async Task<bool> Wait(Func<bool> cond, int ms)
        {
            for (int t = 0; t < ms; t += 100) { if (cond()) return true; await Task.Delay(100); }
            return cond();
        }

        static void Snap(Form f, string path, bool fromScreen = false)
        {
            if (f == null) return;
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                if (fromScreen) { f.Activate(); using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size); }
                else f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); // works even if other windows cover it
                bmp.Save(path);
            }
        }

        /// <summary>--inputtest: injects a click and two key presses into a small test window (cursor is restored).</summary>
        public static void RunInput(string outDir)
        {
            var f = new Form { Text = "KilatDesk input test", StartPosition = FormStartPosition.CenterScreen, Size = new Size(460, 220), TopMost = true };
            var tb = new TextBox { Location = new Point(20, 20), Width = 300 };
            var btn = new Button { Text = "Klik", Location = new Point(20, 70), Size = new Size(140, 50) };
            int clicks = 0;
            btn.Click += (o, e) => clicks++;
            f.Controls.Add(tb); f.Controls.Add(btn);
            f.Shown += async (s, e) =>
            {
                Native.GetCursorPos(out POINT orig);
                try
                {
                    await Task.Delay(600);
                    var target = btn.PointToScreen(new Point(btn.Width / 2, btn.Height / 2));
                    var mons = Monitors.Enumerate();
                    var mon = mons.First(m => target.X >= m.X && target.X < m.X + m.Width && target.Y >= m.Y && target.Y < m.Y + m.Height);
                    var inj = new InputInjector();
                    // 1) single-monitor mapping
                    inj.SetMonitor(mon);
                    inj.MouseMove((ushort)((target.X - mon.X) * 65535L / (mon.Width - 1)), (ushort)((target.Y - mon.Y) * 65535L / (mon.Height - 1)));
                    await Task.Delay(150);
                    Native.GetCursorPos(out POINT now);
                    R.AppendLine("single monitor " + mon + ": target " + target.X + "," + target.Y + " -> " + now.X + "," + now.Y);
                    bool okSingle = Math.Abs(now.X - target.X) <= 1 && Math.Abs(now.Y - target.Y) <= 1;
                    // 2) all-monitors mapping (virtual desktop bounding box)
                    var all = Monitors.Bounds(mons);
                    inj.SetMonitor(all);
                    Native.SetCursorPos(orig.X, orig.Y);
                    await Task.Delay(50);
                    inj.MouseMove((ushort)((target.X - all.X) * 65535L / (all.Width - 1)), (ushort)((target.Y - all.Y) * 65535L / (all.Height - 1)));
                    await Task.Delay(150);
                    Native.GetCursorPos(out POINT now2);
                    R.AppendLine("all monitors " + all.Width + "x" + all.Height + " at " + all.X + "," + all.Y + ": target -> " + now2.X + "," + now2.Y);
                    bool okAll = Math.Abs(now2.X - target.X) <= 2 && Math.Abs(now2.Y - target.Y) <= 2;
                    inj.MouseButton(0, true); inj.MouseButton(0, false);
                    await Task.Delay(300);
                    f.Activate(); tb.Focus();
                    await Task.Delay(200);
                    inj.Key(0x41, 0x1E, 0); inj.Key(0x41, 0x1E, 1);
                    inj.Key(0x42, 0x30, 0); inj.Key(0x42, 0x30, 1);
                    await Task.Delay(300);
                    R.AppendLine("button clicks: " + clicks + ", textbox: '" + tb.Text + "'");
                    R.AppendLine(okSingle && okAll && clicks == 1 && tb.Text.ToLowerInvariant() == "ab" ? "INPUT OK" : "INPUT FAIL");
                }
                catch (Exception ex) { R.AppendLine("INPUT TEST ERROR: " + ex); }
                finally
                {
                    Native.SetCursorPos(orig.X, orig.Y);
                    File.WriteAllText(Path.Combine(outDir, "inputtest.txt"), R.ToString());
                    f.Close();
                }
            };
            Application.Run(f);
        }

        public static void Run(string outDir)
        {
            var form = new MainForm(false);
            form.Shown += async (s, e) =>
            {
                try
                {
                    int hp = FreePort(), vp = FreePort();
                    form.TestSetPorts(hp, vp);
                    await Task.Delay(700);
                    Snap(form, Path.Combine(outDir, "ui-main-viewer.png"));
                    form.TestSelectTab(1);
                    string pw = form.TestHostPassword;
                    form.TestStartHost();
                    await Task.Delay(1500);
                    Snap(form, Path.Combine(outDir, "ui-main-host.png"));
                    Check(form.TestHostStatus.Contains("Aktif"), "host aktif: " + form.TestHostStatus);

                    // ---- normal connection
                    form.TestSelectTab(0);
                    await form.TestConnect("127.0.0.1:" + hp, pw);
                    ViewerForm v = null;
                    await Wait(() => (v = Application.OpenForms.OfType<ViewerForm>().FirstOrDefault()) != null && v.TestSession.FramesTotal > 0, 6000);
                    Check(v != null, "viewer terbuka");
                    if (v == null) return;
                    var vs = v.TestSession;
                    await Wait(() => vs.HasHostStats && vs.AudioRate > 0, 4000);
                    await Task.Delay(800);
                    R.AppendLine("viewer: " + v.Text + " | " + v.TestInfo);
                    R.AppendLine("monitor buttons: " + string.Join(" ", v.TestMonitorButtons));
                    Check(v.TestMonitorButtons.Length == vs.MonitorNames.Length + (vs.MonitorNames.Length > 1 ? 1 : 0), "tombol monitor + Semua tampil");
                    Check(vs.AudioRate > 0, "suara: format " + vs.AudioRate + " Hz, " + vs.AudioPackets + " paket");
                    Snap(v, Path.Combine(outDir, "ui-viewer.png"), fromScreen: true);

                    // ---- monitor switching via the toolbar buttons
                    for (int i = 1; i < v.TestMonitorButtons.Length; i++)
                    {
                        int want = i < vs.MonitorNames.Length ? i : Monitors.All;
                        v.TestClickMonitor(i);
                        bool ok = await Wait(() => vs.Monitor == want, 5000);
                        await Task.Delay(1200);
                        Check(ok && vs.Width > 0, "klik tombol '" + v.TestMonitorButtons[i] + "' -> " + (want == Monitors.All ? "Semua" : "Monitor " + (want + 1)) + " " + vs.Width + "x" + vs.Height + " (" + vs.CaptureKind + ")");
                        if (want == Monitors.All) Snap(v, Path.Combine(outDir, "ui-viewer-all.png"), fromScreen: true);
                        if (i == 2) Snap(v, Path.Combine(outDir, "ui-viewer-mon3.png"), fromScreen: true);
                    }
                    v.TestClickMonitor(0);
                    Check(await Wait(() => vs.Monitor == 0, 5000), "kembali ke monitor 1");

                    // ---- chat both ways
                    vs.SendChat("halo dari viewer");
                    Check(await Wait(() => (form.TestHostChat ?? "").Contains("halo dari viewer"), 3000), "chat viewer -> host (jendela chat host muncul)");
                    form.TestSession?.SendChat("halo juga dari host");
                    Check(await Wait(() => (v.TestChat ?? "").Contains("halo juga dari host"), 3000), "chat host -> viewer");

                    // ---- file viewer -> host
                    string src = Path.Combine(AppPaths.Dir, "uji kirim.txt");
                    File.WriteAllText(src, new string('x', 200_000));
                    string dst = Path.Combine(FileTransfer.DownloadDirOverride, "uji kirim.txt");
                    if (File.Exists(dst)) File.Delete(dst);
                    vs.Files.SendFiles(new[] { src });
                    Check(await Wait(() => File.Exists(dst) && new FileInfo(dst).Length == 200_000, 6000), "kirim file viewer -> host");

                    // ---- privacy screen
                    vs.SetPrivacy(true);
                    Check(await Wait(() => form.TestPrivacyOk && vs.HostPrivacy, 4000), "layar privasi aktif & tidak ikut ter-capture");
                    vs.SetPrivacy(false);
                    Check(await Wait(() => !form.TestPrivacyOk && !vs.HostPrivacy, 4000), "layar privasi mati");

                    // ---- host locks input
                    form.TestSetAllowInput(false);
                    Check(await Wait(() => !vs.HostInputAllowed, 3000), "host mengunci kontrol (viewer tahu)");
                    form.TestSetAllowInput(true);
                    Check(await Wait(() => vs.HostInputAllowed, 3000), "host membuka kontrol lagi");

                    // ---- screenshot
                    string shot = v.TestScreenshot();
                    Check(shot != null && File.Exists(shot), "screenshot tersimpan: " + shot);
                    v.Close();
                    Check(await Wait(() => form.TestHostStatus.Contains("menunggu"), 4000), "sesi selesai setelah viewer ditutup");

                    // ---- approval prompt
                    form.TestSetAskApproval(true);
                    var connecting = form.TestConnect("127.0.0.1:" + hp, pw);
                    ApprovalForm ap = null;
                    await Wait(() => (ap = Application.OpenForms.OfType<ApprovalForm>().FirstOrDefault()) != null, 8000);
                    Check(ap != null, "dialog persetujuan muncul di host");
                    if (ap != null) { Snap(ap, Path.Combine(outDir, "ui-approval.png")); ap.DialogResult = DialogResult.Yes; }
                    await connecting;
                    Check(await Wait(() => (v = Application.OpenForms.OfType<ViewerForm>().FirstOrDefault()) != null, 5000), "viewer terbuka setelah disetujui");
                    v?.Close();
                    form.TestSetAskApproval(false);
                    await Wait(() => form.TestHostStatus.Contains("menunggu"), 4000);

                    // ---- reverse mode
                    form.TestSelectTab(0);
                    form.TestStartViewerListen(pw);
                    await Task.Delay(1000);
                    form.TestSelectTab(1);
                    await form.TestReverse("127.0.0.1:" + vp);
                    Check(await Wait(() => (v = Application.OpenForms.OfType<ViewerForm>().FirstOrDefault()) != null && v.TestSession.FramesTotal > 0, 6000), "mode terbalik: viewer menerima gambar");
                    Snap(form, Path.Combine(outDir, "ui-main-host-session.png"));
                    v?.Close();
                    await Task.Delay(1200);
                    form.TestStopAll();
                    Check(form.TestHostStatus.Contains("Nonaktif"), "host dimatikan");
                }
                catch (Exception ex) { R.AppendLine("UI TEST ERROR: " + ex); _fails++; }
                finally
                {
                    R.AppendLine(_fails == 0 ? "UI RESULT: ALL OK" : "UI RESULT: " + _fails + " FAILURE(S)");
                    File.WriteAllText(Path.Combine(outDir, "uitest.txt"), R.ToString());
                    form.TestExit();
                }
            };
            Application.Run(form);
        }
    }
}
