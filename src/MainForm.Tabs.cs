using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KilatDesk
{
    internal sealed partial class MainForm
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        TabPage BuildViewerTab()
        {
            var page = new TabPage("Kontrol PC lain");
            var t = Grid();
            Row(t, null, Head("Hubungkan ke komputer lain"));
            Row(t, null, Note("Masukkan IP publik (atau IP lokal kalau satu jaringan) dan port komputer yang mau dikontrol, contoh 203.0.113.10:7171."));
            foreach (var h in Settings.Get("viewer.history", "").Split('|').Where(x => x.Length > 0)) _addr.Items.Add(h);
            _addr.Text = _addr.Items.Count > 0 ? (string)_addr.Items[0] : "";
            _addr.TextChanged += (o, e) => { var pw = Settings.GetSecret("viewer.pw." + _addr.Text.Trim()); if (pw.Length > 0) _vpass.Text = pw; };
            _vpass.Text = Settings.GetSecret("viewer.pw." + _addr.Text.Trim());
            var show = new CheckBox { Text = "tampilkan", AutoSize = true };
            show.CheckedChanged += (o, e) => _vpass.UseSystemPasswordChar = !show.Checked;
            Row(t, "Alamat (IP:port)", _addr);
            Row(t, "Password", _vpass, show);
            Row(t, "", _connect, _remember);
            Row(t, null, _vstatus);
            _connect.Click += async (o, e) => await ConnectAsync();
            AcceptButton = _connect;

            Row(t, null, Head("Mode terbalik"));
            Row(t, null, Note("Pakai ini kalau justru komputer KAMU yang punya IP publik: komputer ini menunggu, lalu komputer tujuan yang menghubungi (tombol \"Hubungkan ke viewer\" di tab sebelah). Password memakai kolom di atas."));
            _vport.Value = Settings.GetInt("viewer.listenPort", 7172);
            _vlistenBtn.Click += (o, e) => ToggleViewerListen();
            Row(t, "Port tunggu", _vport, _vlistenBtn);
            page.Controls.Add(t);
            return page;
        }

        TabPage BuildHostTab()
        {
            var page = new TabPage("Izinkan dikontrol");
            var t = Grid();
            Row(t, null, Head("Izinkan komputer ini dikontrol"));
            Row(t, "Status", _hstatus);
            _hport.Value = Settings.GetInt("host.port", 7171);
            string pw = Settings.GetSecret("host.password");
            if (pw.Length < 8) { pw = Util.NewPassword(); Settings.SetSecret("host.password", pw); }
            _hpass.Text = pw;
            Row(t, "Port", _hport);
            Row(t, "Password", _hpass,
                Btn("Acak", (o, e) => _hpass.Text = Util.NewPassword()),
                Btn("Salin", (o, e) => { try { Clipboard.SetText(_hpass.Text); } catch { } }));
            _autostart.Checked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("KilatDesk") != null;
            _autostart.CheckedChanged += (o, e) => SetAutostart(_autostart.Checked);
            Row(t, "", _autostart);
            _allowInput.Checked = Settings.GetInt("host.allowInput", 1) != 0;
            HostSession.AllowInput = _allowInput.Checked;
            _allowInput.CheckedChanged += (o, e) => { HostSession.AllowInput = _allowInput.Checked; Settings.Set("host.allowInput", _allowInput.Checked ? "1" : "0"); };
            _askApproval.Checked = Settings.GetInt("host.askApproval", 0) != 0;
            _askApproval.CheckedChanged += (o, e) => { Settings.Set("host.askApproval", _askApproval.Checked ? "1" : "0"); ApplyApproval(); };
            ApplyApproval();
            Row(t, "", _allowInput);
            Row(t, "", _askApproval);
            _hostBtn.Click += (o, e) => { if (_host.IsListening) StopHost(); else StartHost(false); };
            Row(t, "", _hostBtn, Btn("Izinkan di Firewall", (o, e) => AddFirewallRule()), Btn("Buka port di router (UPnP)", async (o, e) => await UpnpAsync()));
            Row(t, "Alamat kamu", _addrInfo, Btn("Cek IP publik", async (o, e) => await CheckPublicIpAsync()));
            Row(t, "Sidik jari", _fp);
            Row(t, "Mode terbalik", _reverseAddr, Btn("Hubungkan ke viewer", async (o, e) => await ReverseConnectAsync()));
            _reverseAddr.Text = Settings.Get("host.reverseAddr", "");
            _sendFileBtn.Click += (o, e) => SendFileToViewer();
            _hostChatBtn.Click += (o, e) => OpenHostChat();
            _endSessionBtn.Click += (o, e) => _host.Disconnect();
            Row(t, "Sesi aktif", _sendFileBtn, _hostChatBtn, _endSessionBtn,
                Btn("Folder file masuk", (o, e) => { System.IO.Directory.CreateDirectory(FileTransfer.DownloadDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + FileTransfer.DownloadDir + "\""); }));
            int er = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _events.MinimumSize = new Size(0, 70);
            _events.Margin = new Padding(0, 8, 0, 0);
            t.Controls.Add(_events, 0, er);
            t.SetColumnSpan(_events, 2);
            page.Controls.Add(t);
            UpdateHostStatus();
            return page;
        }

        void UpdateHostStatus()
        {
            var s = _host.Session;
            if (s != null) { _hstatus.Text = "● Sedang dikontrol dari " + s.Remote; _hstatus.ForeColor = Color.Firebrick; }
            else if (_host.IsListening) { _hstatus.Text = "● Aktif - menunggu koneksi di port " + _host.Port; _hstatus.ForeColor = Color.ForestGreen; }
            else { _hstatus.Text = "● Nonaktif"; _hstatus.ForeColor = Color.DimGray; }
            _hostBtn.Text = _host.IsListening ? "Nonaktifkan" : "Aktifkan";
            _sendFileBtn.Enabled = _hostChatBtn.Enabled = _endSessionBtn.Enabled = s != null;
            _addrInfo.Text = "IP lokal: " + LocalIPv4() + "   port " + (int)_hport.Value;
            _tray.Text = s != null ? "KilatDesk - dikontrol" : _host.IsListening ? "KilatDesk - siap dikontrol" : "KilatDesk";
        }

        async void StartHost(bool silent)
        {
            string pw = _hpass.Text.Trim();
            if (pw.Length < 8) { MessageBox.Show(this, "Password minimal 8 karakter (lebih panjang lebih aman).", "KilatDesk"); return; }
            int port = (int)_hport.Value;
            _hostBtn.Enabled = false;
            try
            {
                await Task.Run(() => _host.StartListening(port, pw));
                Settings.SetSecret("host.password", pw);
                Settings.Set("host.port", port.ToString());
                AddEvent("Aktif di port " + port);
                if (!silent) _tray.ShowBalloonTip(2500, "KilatDesk aktif", "Komputer ini siap dikontrol di port " + port, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AddEvent("Gagal aktif: " + ex.Message);
                if (!silent) MessageBox.Show(this, "Tidak bisa membuka port " + port + ":\n" + ex.Message + "\n\nMungkin port dipakai aplikasi lain.", "KilatDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _hostBtn.Enabled = true; UpdateHostStatus(); }
        }

        void StopHost()
        {
            _host.Disconnect();
            _host.StopListening();
            AddEvent("Dinonaktifkan");
            UpdateHostStatus();
        }

        void OnSessionStarted(HostSession s)
        {
            s.ClipboardFromViewer = text => Ui.Post(this, () => _clip?.SetText(text));
            s.ChatReceived += (x, text) => Ui.Post(this, () => EnsureHostChat(x).Incoming(text));
            s.PrivacyRequested += (x, on) => Ui.Post(this, () => SetPrivacy(x, on));
            s.Files.Status += text => Ui.Post(this, () => AddEvent(text));
            s.Files.Received += path => Ui.Post(this, () =>
                _tray.ShowBalloonTip(4000, "KilatDesk - file diterima", System.IO.Path.GetFileName(path) + "\nTersimpan di " + FileTransfer.DownloadDir, ToolTipIcon.Info));
            _banner.ShowFor(s.Remote);
            AddEvent("Terhubung: " + s.Remote);
            _tray.ShowBalloonTip(3000, "KilatDesk", "Komputer ini sedang dikontrol dari " + s.Remote, ToolTipIcon.Warning);
            UpdateHostStatus();
        }

        void OnSessionEnded(HostSession s, string reason)
        {
            _banner.Hide();
            _privacy.Hide();
            if (_hostChat != null) { _hostChat.AllowClose = true; _hostChat.Close(); _hostChat = null; }
            AddEvent("Sesi selesai (" + s.Remote + "): " + reason);
            UpdateHostStatus();
        }

        void SetPrivacy(HostSession s, bool on)
        {
            if (on) { _privacy.Show(); s.PrivacyActive = _privacy.VerifyExcluded(); if (!s.PrivacyActive) _privacy.Hide(); }
            else { _privacy.Hide(); s.PrivacyActive = false; }
            AddEvent(s.PrivacyActive ? "Layar privasi AKTIF (diminta viewer)" : "Layar privasi mati");
        }

        ChatForm EnsureHostChat(HostSession s)
        {
            if (_hostChat == null || _hostChat.IsDisposed)
                _hostChat = new ChatForm(s.Remote, "Saya", "Viewer", text => _host.Session?.SendChat(text));
            return _hostChat;
        }

        void OpenHostChat()
        {
            var s = _host.Session;
            if (s != null) EnsureHostChat(s).Open();
        }

        void SendFileToViewer()
        {
            var s = _host.Session;
            if (s == null) return;
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Kirim file ke viewer (" + s.Remote + ")" })
                if (dlg.ShowDialog(this) == DialogResult.OK) s.Files.SendFiles(dlg.FileNames);
        }

        /// <summary>When enabled, every new connection (with the right password) still needs a click from the person at this PC.</summary>
        void ApplyApproval()
        {
            _host.ApproveConnection = !_askApproval.Checked ? (Func<string, bool>)null : remote =>
            {
                if (!IsHandleCreated) return false;
                return (bool)Invoke(new Func<bool>(() =>
                {
                    using (var f = new ApprovalForm(remote)) return f.ShowDialog() == DialogResult.Yes;
                }));
            };
        }

        async Task ConnectAsync()
        {
            string text = _addr.Text.Trim();
            if (!Util.TryParseEndpoint(text, 7171, out string host, out int port)) { MessageBox.Show(this, "Alamat tidak valid. Contoh: 203.0.113.10:7171", "KilatDesk"); return; }
            string pw = _vpass.Text;
            _connect.Enabled = false;
            _vstatus.Text = "Menghubungkan ke " + host + ":" + port + " ...";
            try
            {
                var conn = await Task.Run(() => Secure.Dial(host, port, AppRole.Viewer, pw, ConfirmFingerprint));
                var hist = new[] { text }.Concat(Settings.Get("viewer.history", "").Split('|').Where(x => x.Length > 0 && x != text)).Take(10).ToArray();
                Settings.Set("viewer.history", string.Join("|", hist));
                if (_remember.Checked) Settings.SetSecret("viewer.pw." + text, pw); else Settings.Set("viewer.pw." + text, "");
                if (!_addr.Items.Contains(text)) _addr.Items.Insert(0, text);
                _vstatus.Text = "Terhubung ke " + host + ":" + port;
                OpenViewer(conn);
            }
            catch (Exception ex)
            {
                _vstatus.Text = "Gagal: " + ex.Message;
                MessageBox.Show(this, ex.Message, "KilatDesk - gagal terhubung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _connect.Enabled = true; }
        }

        void OpenViewer(Connection conn)
        {
            try { new ViewerForm(new ViewerSession(conn)).Show(); }
            catch (Exception ex) { conn.Bye("Viewer error"); MessageBox.Show(this, ex.Message, "KilatDesk"); }
        }

        async void ToggleViewerListen()
        {
            if (_vlisten.IsListening) { _vlisten.Stop(); _vlistenBtn.Text = "Mulai menunggu"; _vstatus.Text = "Berhenti menunggu."; return; }
            string pw = _vpass.Text.Trim();
            if (pw.Length < 8) { MessageBox.Show(this, "Isi password (minimal 8 karakter) di kolom Password di atas. Komputer tujuan harus memakai password yang sama.", "KilatDesk"); return; }
            int port = (int)_vport.Value;
            try
            {
                await Task.Run(() => _vlisten.Start(port, pw));
                Settings.Set("viewer.listenPort", port.ToString());
                _vlistenBtn.Text = "Berhenti menunggu";
                _vstatus.Text = "Menunggu komputer tujuan menghubungi port " + port + " ...";
            }
            catch (Exception ex) { MessageBox.Show(this, "Tidak bisa membuka port " + port + ": " + ex.Message, "KilatDesk"); }
        }

        async Task ReverseConnectAsync()
        {
            string text = _reverseAddr.Text.Trim();
            if (!Util.TryParseEndpoint(text, 7172, out string host, out int port)) { MessageBox.Show(this, "Isi alamat viewer, contoh 203.0.113.10:7172", "KilatDesk"); return; }
            string pw = _hpass.Text.Trim();
            AddEvent("Menghubungi viewer " + host + ":" + port + " ...");
            try
            {
                await Task.Run(() => _host.ConnectOut(host, port, pw, ConfirmFingerprint));
                Settings.Set("host.reverseAddr", text);
            }
            catch (Exception ex) { AddEvent("Gagal: " + ex.Message); MessageBox.Show(this, ex.Message, "KilatDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            UpdateHostStatus();
        }

        void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("KilatDesk", "\"" + Application.ExecutablePath + "\" --host");
                    else k.DeleteValue("KilatDesk", false);
                }
                AddEvent(on ? "Autostart aktif" : "Autostart dimatikan");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "KilatDesk"); }
        }

        void AddFirewallRule()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall add rule name=\"KilatDesk\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=any")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) { p.WaitForExit(15000); AddEvent(p.ExitCode == 0 ? "Aturan firewall ditambahkan" : "netsh exit code " + p.ExitCode); }
            }
            catch (Exception ex) { AddEvent("Firewall: " + ex.Message); }
        }

        async Task UpnpAsync()
        {
            int port = (int)_hport.Value;
            string local = LocalIPv4().Split(',')[0].Trim();
            AddEvent("Mencoba UPnP di router untuk port " + port + " ...");
            string result = await Task.Run(() =>
            {
                try
                {
                    dynamic nat = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP"));
                    dynamic maps = nat.StaticPortMappingCollection;
                    if (maps == null) return "Router tidak mendukung / UPnP dimatikan. Atur port forwarding manual di router.";
                    maps.Add(port, "TCP", port, local, true, "KilatDesk");
                    return "UPnP OK: port " + port + " diteruskan ke " + local;
                }
                catch (Exception ex) { return "UPnP gagal: " + ex.Message; }
            });
            AddEvent(result);
        }

        async Task CheckPublicIpAsync()
        {
            try
            {
                string ip = await Task.Run(() => { using (var wc = new WebClient()) return wc.DownloadString("https://api.ipify.org").Trim(); });
                _addrInfo.Text = "IP publik: " + ip + ":" + (int)_hport.Value + "   |   IP lokal: " + LocalIPv4();
                AddEvent("IP publik: " + ip + " (viewer menghubungkan ke " + ip + ":" + (int)_hport.Value + ")");
            }
            catch (Exception ex) { AddEvent("Cek IP publik gagal: " + ex.Message); }
        }
    }
}
