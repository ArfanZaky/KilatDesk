using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KilatDesk
{
    internal sealed partial class MainForm : Form
    {
        readonly HostService _host = new HostService();
        readonly ViewerListener _vlisten = new ViewerListener();
        readonly HostBanner _banner = new HostBanner();
        readonly NotifyIcon _tray = new NotifyIcon();
        ClipboardWatcher _clip;
        readonly bool _autoHost;
        bool _exiting;

        // viewer tab
        readonly ComboBox _addr = new ComboBox { Width = 260 };
        readonly TextBox _vpass = new TextBox { Width = 260, UseSystemPasswordChar = true };
        readonly CheckBox _remember = new CheckBox { Text = "Ingat password", AutoSize = true, Checked = true };
        readonly Button _connect = new Button { Text = "Hubungkan", AutoSize = true, Font = Ui.Bold };
        readonly Label _vstatus = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly NumericUpDown _vport = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 80 };
        readonly Button _vlistenBtn = new Button { Text = "Mulai menunggu", AutoSize = true };

        // host tab
        readonly Label _hstatus = new Label { AutoSize = true, Font = Ui.Bold };
        readonly NumericUpDown _hport = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 80 };
        readonly TextBox _hpass = new TextBox { Width = 200, Font = new Font("Consolas", 10f) };
        readonly CheckBox _autostart = new CheckBox { Text = "Aktif otomatis saat login Windows (siap dikontrol tanpa ada orang)", AutoSize = true };
        readonly Button _hostBtn = new Button { Text = "Aktifkan", AutoSize = true, Font = Ui.Bold };
        readonly Label _addrInfo = new Label { AutoSize = true };
        readonly Label _fp = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly TextBox _reverseAddr = new TextBox { Width = 200 };
        readonly ListBox _events = new ListBox { Height = 90, Dock = DockStyle.Fill, IntegralHeight = false };
        readonly CheckBox _allowInput = new CheckBox { Text = "Izinkan viewer mengontrol mouse && keyboard (kalau dimatikan: viewer hanya bisa melihat)", AutoSize = true };
        readonly CheckBox _askApproval = new CheckBox { Text = "Minta persetujuan saya setiap ada yang terhubung (jangan dicentang untuk akses tanpa orang)", AutoSize = true };
        readonly Button _sendFileBtn = new Button { Text = "Kirim file ke viewer", AutoSize = true, Enabled = false };
        readonly Button _hostChatBtn = new Button { Text = "Chat", AutoSize = true, Enabled = false };
        readonly Button _endSessionBtn = new Button { Text = "Putuskan sesi", AutoSize = true, Enabled = false };
        readonly PrivacyOverlay _privacy = new PrivacyOverlay();
        ChatForm _hostChat;

        public MainForm(bool autoHost)
        {
            _autoHost = autoHost;
            SuspendLayout(); // same pattern as designer code so WinForms DPI auto-scaling kicks in on ResumeLayout
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "KilatDesk - Remote Desktop Cepat";
            Icon = Ui.AppIcon;
            Font = Ui.Normal;
            ClientSize = new Size(700, 620);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
            tabs.TabPages.Add(BuildViewerTab());
            tabs.TabPages.Add(BuildHostTab());
            Controls.Add(tabs);
            if (_autoHost) tabs.SelectedIndex = 1;
            // stable names = UI Automation ids (used by the UI test script)
            _addr.Name = "viewerAddress"; _vpass.Name = "viewerPassword"; _connect.Name = "connectButton";
            _hpass.Name = "hostPassword"; _hostBtn.Name = "hostButton"; _hport.Name = "hostPort"; _hstatus.Name = "hostStatus";

            _tray.Icon = Ui.AppIcon;
            _tray.Text = "KilatDesk";
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Buka KilatDesk", null, (o, e) => ShowFromTray());
            menu.Items.Add("Putuskan sesi", null, (o, e) => _host.Disconnect());
            menu.Items.Add("Chat dengan viewer", null, (o, e) => OpenHostChat());
            menu.Items.Add("Matikan layar privasi", null, (o, e) => { _privacy.Hide(); var s = _host.Session; if (s != null) s.PrivacyActive = false; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Keluar", null, (o, e) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (o, e) => ShowFromTray();

            _banner.DisconnectClicked += () => _host.Disconnect();
            _banner.ChatClicked += () => OpenHostChat();
            _host.Status += s => Ui.Post(this, () => AddEvent(s));
            _host.SessionStarted += s => Ui.Post(this, () => OnSessionStarted(s));
            _host.SessionEnded += (s, r) => Ui.Post(this, () => OnSessionEnded(s, r));
            _vlisten.Status += s => Ui.Post(this, () => _vstatus.Text = s);
            _vlisten.Connected += c => Ui.Post(this, () => OpenViewer(c));
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _clip = new ClipboardWatcher();
            _clip.TextCopied += t => _host.Session?.SendClipboard(t);
            Task.Run(() =>
            {
                try { string f = Secure.MyFingerprint; Ui.Post(this, () => _fp.Text = f); }
                catch (Exception ex) { Log.Error("sertifikat", ex); Ui.Post(this, () => _fp.Text = "ERROR: " + ex.Message); }
            });
            if (_autoHost) BeginInvoke(new Action(() => { StartHost(true); }));
        }

        protected override void SetVisibleCore(bool value)
        {
            // when started at login with --host we stay in the tray
            if (_autoHost && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        // ------------------------------------------------------------------ layout helpers
        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        static void Row(TableLayoutPanel t, string label, params Control[] cs)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label != null) t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 10, 4) }, 0, r);
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            foreach (var c in cs) { c.Margin = new Padding(0, 3, 8, 3); flow.Controls.Add(c); }
            t.Controls.Add(flow, label == null ? 0 : 1, r);
            if (label == null) t.SetColumnSpan(flow, 2);
        }

        static Label Head(string s) => new Label { Text = s, AutoSize = true, Font = Ui.Title, ForeColor = Ui.Accent, Margin = new Padding(0, 6, 0, 2) };
        static Label Note(string s) => new Label { Text = s, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DimGray };
        static Button Btn(string s, EventHandler click) { var b = new Button { Text = s, AutoSize = true }; b.Click += click; return b; }

        void AddEvent(string s)
        {
            _events.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + s);
            while (_events.Items.Count > 200) _events.Items.RemoveAt(_events.Items.Count - 1);
        }

        static string LocalIPv4()
        {
            try
            {
                var ips = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Where(n => n.GetIPProperties().GatewayAddresses.Any())
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToArray();
                return ips.Length > 0 ? string.Join(", ", ips) : "-";
            }
            catch { return "-"; }
        }

        bool ConfirmFingerprint(string old, string now)
        {
            return (bool)Invoke(new Func<bool>(() => MessageBox.Show(this,
                "PERINGATAN: sertifikat komputer tujuan BERUBAH.\n\nSebelumnya: " + old + "\nSekarang:   " + now +
                "\n\nIni wajar kalau KilatDesk di komputer itu baru di-install ulang. Kalau tidak, bisa jadi ada pihak lain yang mencegat koneksi.\n\nTetap lanjutkan?",
                "KilatDesk - keamanan", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes));
        }

        internal void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            _exiting = true;
            try { _host.Disconnect("KilatDesk di host ditutup"); } catch { }
            _host.StopListening(); _vlisten.Stop();
            _tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing && _host.IsListening)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(3000, "KilatDesk masih aktif", "Komputer ini tetap bisa dikontrol. Klik kanan ikon di tray untuk keluar.", ToolTipIcon.Info);
                return;
            }
            _exiting = true;
            _host.Disconnect("KilatDesk di host ditutup");
            _host.StopListening(); _vlisten.Stop();
            _clip?.Dispose();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
