using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace KilatDesk
{
    /// <summary>Black surface we paint ourselves with GDI from a render thread.</summary>
    internal sealed class RenderPanel : Control
    {
        public event Action<short> HWheel;
        public RenderPanel()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.Black;
        }
        protected override bool IsInputKey(Keys keyData) => true;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020E) HWheel?.Invoke((short)((long)m.WParam >> 16)); // WM_MOUSEHWHEEL
            base.WndProc(ref m);
        }
    }

    internal sealed unsafe partial class ViewerForm : Form
    {
        readonly ViewerSession _s;
        readonly RenderPanel _panel = new RenderPanel { Dock = DockStyle.Fill, AllowDrop = true };
        readonly ToolStrip _bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, RenderMode = ToolStripRenderMode.System };
        readonly StatusStrip _statusBar = new StatusStrip { SizingGrip = false };
        readonly ToolStripStatusLabel _status = new ToolStripStatusLabel("Siap. Seret file ke jendela ini untuk mengirimnya.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly Label _hint = new Label { Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White, Visible = false, Font = Ui.Bold };
        readonly Timer _timer = new Timer { Interval = 1000 };
        readonly Timer _hintTimer = new Timer { Interval = 1800 };
        readonly HashSet<uint> _down = new HashSet<uint>();
        readonly AutoResetEvent _redraw = new AutoResetEvent(false);
        int _drawn, _drawnLast;
        double _drawFps;
        KeyboardHook _hook;
        ClipboardWatcher _clip;
        ChatForm _chat;
        volatile IntPtr _hwndPanel;
        IntPtr _hwndForm;
        volatile int _cw, _ch;
        bool _ctrl, _alt, _fullscreen, _closing, _sized;
        int _lastMonitorShown = int.MinValue;
        string _statusPath;
        FormBorderStyle _prevBorder; FormWindowState _prevState; Rectangle _prevBounds;

        public ViewerForm(ViewerSession s)
        {
            _s = s;
            Text = "KilatDesk - " + s.Conn.Remote;
            Icon = Ui.AppIcon;
            KeyPreview = false;
            AllowDrop = true;
            _bar.BackColor = SystemColors.Control;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(wa.Width * 3 / 4, wa.Height * 3 / 4);
            StartPosition = FormStartPosition.CenterScreen;

            BuildToolbar();
            _statusBar.Items.Add(_status);
            _statusBar.Items.Add(_stats); // ping | fps | Mbps on the right of the status bar
            _status.Click += (o, e) => OpenStatusPath();
            Controls.Add(_panel);
            Controls.Add(_hint);
            Controls.Add(_bar);
            Controls.Add(_statusBar);

            _panel.Resize += (o, e) => { _cw = _panel.ClientSize.Width; _ch = _panel.ClientSize.Height; _redraw.Set(); };
            _panel.Paint += (o, e) => _redraw.Set();
            _panel.MouseMove += (o, e) => { if (Map(e.X, e.Y, out ushort x, out ushort y)) _s.MouseMove(x, y); };
            _panel.MouseDown += (o, e) =>
            {
                _panel.Focus();
                if (Map(e.X, e.Y, out ushort x, out ushort y)) _s.MouseMove(x, y);
                byte b = Btn(e.Button); if (b < 5) _s.MouseButton(b, true);
            };
            _panel.MouseUp += (o, e) => { byte b = Btn(e.Button); if (b < 5) _s.MouseButton(b, false); };
            _panel.MouseWheel += (o, e) => _s.Wheel((short)e.Delta, false);
            _panel.HWheel += d => _s.Wheel(d, true);
            _panel.KeyDown += (o, e) => { if (_hook == null || !_hook.Installed) { _s.Key((ushort)e.KeyValue, (ushort)Native.MapVirtualKeyW((uint)e.KeyValue, 0), false, false); e.Handled = true; } };
            _panel.KeyUp += (o, e) => { if (_hook == null || !_hook.Installed) { _s.Key((ushort)e.KeyValue, (ushort)Native.MapVirtualKeyW((uint)e.KeyValue, 0), false, true); e.Handled = true; } };
            foreach (Control c in new Control[] { this, _panel })
            {
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }

            _s.FrameDecoded += OnFrame;
            _s.FormatChanged += () => Ui.Post(this, OnFormat);
            _s.HostInfoReceived += () => Ui.Post(this, OnHostInfo);
            _s.HostStatsReceived += () => Ui.Post(this, OnHostStats);
            _s.CursorChanged += id => Ui.Post(this, () => _panel.Cursor = CursorShapes.ToCursor(id));
            _s.ClipboardReceived += t => Ui.Post(this, () => { if (ClipboardOn) _clip?.SetText(t); });
            _s.ChatReceived += t => Ui.Post(this, () => { EnsureChat().Incoming(t); if (_chat.ContainsFocus == false) _chatBtn.Text = "Chat (pesan baru)"; });
            _s.Files.Status += t => Ui.Post(this, () => SetStatus(t, null));
            _s.Files.Received += p => Ui.Post(this, () => SetStatus("File diterima: " + p + "   (klik untuk membuka foldernya)", p));
            _s.Closed += reason => Ui.Post(this, () =>
            {
                if (_closing) return;
                _closing = true;
                MessageBox.Show(this, "Koneksi berakhir: " + reason, "KilatDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            });
            _timer.Tick += (o, e) => UpdateStats();
            _hintTimer.Tick += (o, e) => { _hintTimer.Stop(); _hint.Visible = false; };
            Activated += (o, e) => _panel.Focus();
            Deactivate += (o, e) => ReleaseKeys();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _hwndForm = Handle;
            _hwndPanel = _panel.Handle;
            _cw = _panel.ClientSize.Width; _ch = _panel.ClientSize.Height;
            _hook = new KeyboardHook(OnKey);
            _clip = new ClipboardWatcher();
            _clip.TextCopied += t => { if (ClipboardOn) _s.SendClipboard(t); };
            _timer.Start();
            new Thread(RenderLoop) { IsBackground = true, Name = "render" }.Start();
            _s.Start();
        }

        void SetStatus(string text, string path)
        {
            _status.Text = text;
            _statusPath = path;
            _status.IsLink = path != null;
        }

        void OpenStatusPath()
        {
            if (_statusPath == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + _statusPath + "\""); } catch { }
        }

        void ShowHint(string text, int ms = 1800)
        {
            _hint.Text = text;
            _hint.Visible = true;
            _hintTimer.Stop(); _hintTimer.Interval = ms; _hintTimer.Start();
        }

        void OnFormat()
        {
            if (!_sized && !_fullscreen && _s.Width > 0)
            {
                _sized = true;
                var wa = Screen.FromControl(this).WorkingArea;
                int extraW = Width - _panel.ClientSize.Width, extraH = Height - _panel.ClientSize.Height;
                double k = Math.Min(1.0, Math.Min((wa.Width * 0.92 - extraW) / _s.Width, (wa.Height * 0.92 - extraH) / _s.Height));
                Size = new Size((int)(_s.Width * k) + extraW, (int)(_s.Height * k) + extraH);
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }
            if (_s.Monitor != _lastMonitorShown)
            {
                if (_lastMonitorShown != int.MinValue) ShowHint(MonitorLabel(_s.Monitor) + "   (" + _s.Width + "x" + _s.Height + ")");
                _lastMonitorShown = _s.Monitor;
            }
            UpdateMonitorButtons();
            _redraw.Set();
            UpdateStats();
        }

        void UpdateStats()
        {
            _s.UpdateStats();
            int drawn = Volatile.Read(ref _drawn);
            _drawFps = drawn - _drawnLast; _drawnLast = drawn;
            string t = (_s.Rtt >= 0 ? _s.Rtt.ToString("0") + " ms" : "-") + "  |  " + _s.FpsShown.ToString("0") + " fps (tampil " + _drawFps.ToString("0") + ")  |  " +
                       _s.MbpsIn.ToString("0.0") + " Mbps  |  " + _s.Width + "x" + _s.Height + "  " + _s.EncoderName;
            if (_s.HasHostStats && _s.HostAuto) t += "  |  auto " + (_s.HostKbps / 1000.0).ToString("0.0") + " Mbps";
            if (_s.HasHostStats && !_s.HostInputAllowed) t += "  |  kontrol dikunci host";
            if (_s.ViewOnly) t += "  |  hanya lihat";
            _stats.Text = t;
        }

        static byte Btn(MouseButtons b) =>
            b == MouseButtons.Left ? (byte)0 : b == MouseButtons.Right ? (byte)1 : b == MouseButtons.Middle ? (byte)2 :
            b == MouseButtons.XButton1 ? (byte)3 : b == MouseButtons.XButton2 ? (byte)4 : (byte)255;

        Rectangle Dest(int w, int h)
        {
            int cw = _cw, ch = _ch;
            if (w <= 0 || h <= 0 || cw <= 0 || ch <= 0) return Rectangle.Empty;
            double k = Math.Min((double)cw / w, (double)ch / h);
            int dw = Math.Max(1, (int)Math.Round(w * k)), dh = Math.Max(1, (int)Math.Round(h * k));
            return new Rectangle((cw - dw) / 2, (ch - dh) / 2, dw, dh);
        }

        bool Map(int px, int py, out ushort x, out ushort y)
        {
            x = y = 0;
            var d = Dest(_s.Width, _s.Height);
            if (d.Width <= 0) return false;
            double fx = Math.Max(0, Math.Min(1, (px - d.X + 0.5) / d.Width)), fy = Math.Max(0, Math.Min(1, (py - d.Y + 0.5) / d.Height));
            x = (ushort)(fx * 65535); y = (ushort)(fy * 65535);
            return true;
        }

        void OnFrame() => _redraw.Set();

        /// <summary>Own thread: grabs the newest decoded picture (scaled by our multi-core scaler while holding the
        /// frame lock briefly) and blits it 1:1. Decoding never waits for drawing; old frames are skipped.</summary>
        void RenderLoop()
        {
            IntPtr buf = IntPtr.Zero; int cap = 0;
            try
            {
                while (!_closing)
                {
                    _redraw.WaitOne(250);
                    IntPtr hwnd = _hwndPanel;
                    int cw = _cw, ch = _ch;
                    if (_closing || hwnd == IntPtr.Zero || cw <= 0 || ch <= 0) continue;
                    Rectangle d = Rectangle.Empty;
                    lock (_s.FrameLock)
                    {
                        int w = _s.Width, h = _s.Height;
                        if (w >= 2 && h >= 2 && _s.Frame != IntPtr.Zero)
                        {
                            d = Dest(w, h);
                            int need = d.Width * d.Height * 4;
                            if (need > cap) { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); buf = Marshal.AllocHGlobal(need); cap = need; }
                            if (d.Width == w && d.Height == h) Buffer.MemoryCopy((void*)_s.Frame, (void*)buf, need, need);
                            else ColorConvert.ScaleBgra((byte*)_s.Frame, w, h, w * 4, (byte*)buf, d.Width, d.Height, d.Width * 4);
                        }
                    }
                    IntPtr hdc = Native.GetDC(hwnd);
                    if (hdc == IntPtr.Zero) continue;
                    try
                    {
                        if (d.IsEmpty) { Native.PatBlt(hdc, 0, 0, cw, ch, Native.BLACKNESS); continue; }
                        if (d.X > 0) { Native.PatBlt(hdc, 0, 0, d.X, ch, Native.BLACKNESS); Native.PatBlt(hdc, d.Right, 0, cw - d.Right, ch, Native.BLACKNESS); }
                        if (d.Y > 0) { Native.PatBlt(hdc, 0, 0, cw, d.Y, Native.BLACKNESS); Native.PatBlt(hdc, 0, d.Bottom, cw, ch - d.Bottom, Native.BLACKNESS); }
                        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = d.Width, biHeight = -d.Height, biPlanes = 1, biBitCount = 32 };
                        Native.SetStretchBltMode(hdc, 3);
                        Native.StretchDIBits(hdc, d.X, d.Y, d.Width, d.Height, 0, 0, d.Width, d.Height, (void*)buf, ref bmi, 0, Native.SRCCOPY);
                        Interlocked.Increment(ref _drawn);
                    }
                    finally { Native.ReleaseDC(hwnd, hdc); }
                }
            }
            catch (Exception ex) { Log.Error("render", ex); }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Keyboard hook callback. Returns true to keep the key away from this PC.</summary>
        bool OnKey(uint vk, uint scan, bool ext, bool up)
        {
            if (_closing || Native.GetForegroundWindow() != _hwndForm) return false;
            if (vk == 0xA2 || vk == 0xA3 || vk == 0x11) _ctrl = !up;
            if (vk == 0xA4 || vk == 0xA5 || vk == 0x12) _alt = !up;
            if (_ctrl && _alt)
            {
                if (vk == 0x0D) { if (!up) Ui.Post(this, ToggleFullscreen); return true; }        // Ctrl+Alt+Enter
                if (vk == 0x25) { if (!up) Ui.Post(this, () => CycleMonitor(-1)); return true; }  // Ctrl+Alt+Left
                if (vk == 0x27) { if (!up) Ui.Post(this, () => CycleMonitor(+1)); return true; }  // Ctrl+Alt+Right
            }
            if (_s.ViewOnly) return false;
            if (up) _down.Remove(vk); else _down.Add(vk);
            _s.Key((ushort)vk, (ushort)scan, ext, up);
            return true;
        }

        void ReleaseKeys()
        {
            if (_down.Count > 0) _s.ReleaseAll();
            _down.Clear(); _ctrl = _alt = false;
        }

        void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _prevBorder = FormBorderStyle; _prevState = WindowState; _prevBounds = Bounds;
                _bar.Visible = false; _statusBar.Visible = false;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _fullscreen = true;
                ShowHint("Layar penuh.  Ctrl+Alt+Enter = keluar   |   Ctrl+Alt+Kiri/Kanan = ganti monitor", 3000);
            }
            else
            {
                FormBorderStyle = _prevBorder;
                Bounds = _prevBounds;
                WindowState = _prevState;
                _bar.Visible = true; _statusBar.Visible = true;
                _fullscreen = false;
            }
            _panel.Focus();
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && _s.Has(HostFeatures.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
            if (!_s.Has(HostFeatures.Files)) { SetStatus("PC tujuan masih KilatDesk versi lama (belum bisa terima file).", null); return; }
            _s.Files.SendFiles(files);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            _hwndPanel = IntPtr.Zero;
            _redraw.Set();
            _s.FrameDecoded -= OnFrame;
            _timer.Stop();
            _hook?.Dispose(); _hook = null;
            _clip?.Dispose(); _clip = null;
            if (_chat != null) { _chat.AllowClose = true; _chat.Close(); _chat = null; }
            var s = _s;
            ThreadPool.QueueUserWorkItem(state => { try { s.Dispose(); } catch { } });
            base.OnFormClosing(e);
        }
    }
}
