using System;
using System.Drawing;
using System.Windows.Forms;

namespace KilatDesk
{
    internal static class Ui
    {
        static Icon _icon;
        public static Icon AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                    if (_icon == null) _icon = SystemIcons.Application;
                }
                return _icon;
            }
        }

        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font Normal = new Font("Segoe UI", 9f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 13f);
        public static readonly Color Accent = Color.FromArgb(0, 102, 204);

        /// <summary>BeginInvoke that is safe while a form is closing.</summary>
        public static void Post(Control c, Action a)
        {
            try { if (c.IsHandleCreated && !c.IsDisposed) c.BeginInvoke(a); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Sends local clipboard text changes and applies remote ones (without echo loops).</summary>
    internal sealed class ClipboardWatcher : NativeWindow, IDisposable
    {
        public event Action<string> TextCopied;
        string _lastSet;

        public ClipboardWatcher()
        {
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // message-only window
            Native.AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) // WM_CLIPBOARDUPDATE
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string t = Clipboard.GetText();
                        if (t != _lastSet && t.Length > 0 && t.Length <= 1_000_000) TextCopied?.Invoke(t);
                    }
                }
                catch { }
            }
            base.WndProc(ref m);
        }

        /// <summary>Must be called on the UI thread.</summary>
        public void SetText(string t)
        {
            if (string.IsNullOrEmpty(t)) return;
            _lastSet = t;
            try { Clipboard.SetDataObject(t, true, 5, 60); } catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Native.RemoveClipboardFormatListener(Handle); DestroyHandle(); }
        }
    }

    /// <summary>Always-on-top strip on the controlled PC so its user always knows someone is connected.
    /// It is excluded from screen capture, so the viewer does not see it.</summary>
    internal sealed class HostBanner : Form
    {
        readonly Label _text;
        public event Action DisconnectClicked, ChatClicked;

        public HostBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(200, 30, 30);
            ForeColor = Color.White;
            Font = Ui.Bold;
            Padding = new Padding(10, 4, 4, 4);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = BackColor };
            _text = new Label { AutoSize = true, Margin = new Padding(0, 7, 12, 0) };
            var btn = new Button { Text = "Putuskan", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(200, 30, 30) };
            btn.Click += (o, e) => DisconnectClicked?.Invoke();
            var chat = new Button { Text = "Chat", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(200, 30, 30) };
            chat.Click += (o, e) => ChatClicked?.Invoke();
            flow.Controls.Add(_text); flow.Controls.Add(chat); flow.Controls.Add(btn);
            Controls.Add(flow);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } // NOACTIVATE | TOOLWINDOW
        }

        protected override bool ShowWithoutActivation => true;

        public void ShowFor(string remote)
        {
            _text.Text = "⚠ Komputer ini sedang dikontrol dari " + remote;
            if (!Visible) Show();
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top);
            Native.SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+)
        }
    }
}
