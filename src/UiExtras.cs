using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace KilatDesk
{
    /// <summary>Small chat window used on both sides.</summary>
    internal sealed class ChatForm : Form
    {
        readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
        readonly TextBox _input = new TextBox { Dock = DockStyle.Fill };
        readonly Action<string> _send;
        readonly string _me, _other;
        public bool AllowClose;
        public event Action Opened;
        internal string TestLog => _log.Text;

        public ChatForm(string title, string me, string other, Action<string> send)
        {
            _send = send; _me = me; _other = other;
            Text = "Chat - " + title;
            Icon = Ui.AppIcon;
            Font = Ui.Normal;
            Size = new Size(420, 380);
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(4) };
            var btn = new Button { Text = "Kirim", Dock = DockStyle.Right, Width = 80 };
            btn.Click += (o, e) => SendNow();
            bottom.Controls.Add(_input);
            bottom.Controls.Add(btn);
            Controls.Add(_log);
            Controls.Add(bottom);
            AcceptButton = btn;
        }

        protected override bool ShowWithoutActivation => true; // an incoming message must not steal the keyboard

        void SendNow()
        {
            string t = _input.Text.Trim();
            if (t.Length == 0) return;
            _send(t);
            Append(_me, t);
            _input.Clear();
        }

        void Append(string who, string text) => _log.AppendText("[" + DateTime.Now.ToString("HH:mm") + "] " + who + ": " + text + "\r\n");

        public void Incoming(string text)
        {
            Append(_other, text);
            if (!Visible) Show();
        }

        public void Open()
        {
            if (!Visible) Show();
            Activate();
            _input.Focus();
            Opened?.Invoke();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// Privacy screen on the controlled PC: black, click-through windows on every monitor that are excluded from
    /// screen capture. People at the PC see black; the viewer still sees (and controls) the real desktop.
    /// </summary>
    internal sealed class PrivacyOverlay
    {
        readonly List<Form> _forms = new List<Form>();
        public bool Active => _forms.Count > 0;

        public void Show()
        {
            if (Active) return;
            foreach (var s in Screen.AllScreens)
            {
                var f = new OverlayForm(s.Bounds);
                f.Show();
                _forms.Add(f);
            }
        }

        public void Hide()
        {
            foreach (var f in _forms) { try { f.Close(); f.Dispose(); } catch { } }
            _forms.Clear();
        }

        /// <summary>True when every overlay is really excluded from capture (else the viewer would see black).</summary>
        public bool VerifyExcluded()
        {
            foreach (var f in _forms)
                if (!Native.GetWindowDisplayAffinity(f.Handle, out uint a) || a != 0x11) return false;
            return Active;
        }

        sealed class OverlayForm : Form
        {
            public OverlayForm(Rectangle bounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.Black;
                Bounds = bounds;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation => true;

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Native.SetLayeredWindowAttributes(Handle, 0, 255, 2 /*LWA_ALPHA*/);
                Native.SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                TextRenderer.DrawText(e.Graphics, "Komputer ini sedang digunakan dari jarak jauh (mode privasi).", Ui.Title,
                    ClientRectangle, Color.FromArgb(110, 110, 110), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>"Someone wants to control this PC" prompt with an automatic reject countdown.</summary>
    internal sealed class ApprovalForm : Form
    {
        int _left;
        readonly Label _count = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly Timer _timer = new Timer { Interval = 1000 };

        public ApprovalForm(string remote, int seconds = 30)
        {
            _left = seconds;
            Text = "KilatDesk - permintaan akses";
            Icon = Ui.AppIcon;
            Font = Ui.Normal;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
            flow.Controls.Add(new Label { Text = "Komputer " + remote + " ingin mengontrol komputer ini.", AutoSize = true, Font = Ui.Bold });
            flow.Controls.Add(new Label { Text = "Password sudah benar. Izinkan koneksi ini?", AutoSize = true, Margin = new Padding(0, 6, 0, 6) });
            flow.Controls.Add(_count);
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            var yes = new Button { Text = "Izinkan", DialogResult = DialogResult.Yes, AutoSize = true, Font = Ui.Bold };
            var no = new Button { Text = "Tolak", DialogResult = DialogResult.No, AutoSize = true };
            buttons.Controls.Add(yes); buttons.Controls.Add(no);
            flow.Controls.Add(buttons);
            Controls.Add(flow);
            AcceptButton = yes; CancelButton = no;
            UpdateCount();
            _timer.Tick += (o, e) => { _left--; UpdateCount(); if (_left <= 0) { DialogResult = DialogResult.No; Close(); } };
            Shown += (o, e) => { Activate(); _timer.Start(); };
            FormClosed += (o, e) => _timer.Dispose();
        }

        void UpdateCount() => _count.Text = "Otomatis ditolak dalam " + _left + " detik.";
    }
}
