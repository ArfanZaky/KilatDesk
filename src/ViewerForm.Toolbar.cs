using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KilatDesk
{
    internal sealed partial class ViewerForm
    {
        readonly ToolStripLabel _hostLabel = new ToolStripLabel("Menghubungkan...") { Font = Ui.Bold };
        readonly ToolStripStatusLabel _stats = new ToolStripStatusLabel("") { ForeColor = Color.DimGray };
        readonly ToolStripLabel _monLabel = new ToolStripLabel("Layar:") { Visible = false };
        readonly List<ToolStripButton> _monButtons = new List<ToolStripButton>();
        ToolStripButton _fileBtn, _chatBtn, _shotBtn;
        ToolStripMenuItem _audioItem, _clipItem, _viewOnlyItem, _privacyItem;
        ToolStripDropDownButton _qualityMenu;
        bool ClipboardOn => _clipItem.Checked;

        void BuildToolbar()
        {
            _bar.Items.Add(_hostLabel);
            _bar.Items.Add(new ToolStripSeparator());
            _bar.Items.Add(_monLabel); // monitor buttons are inserted right after this label
            _bar.Items.Add(new ToolStripSeparator());

            _qualityMenu = MakeMenu("Kualitas", new[] {
                    ("Otomatis - menyesuaikan kecepatan internet (disarankan)", 0),
                    ("Hemat kuota - 2 Mbps", 2000), ("Sedang - 5 Mbps", 5000), ("Tinggi - 8 Mbps", 8000),
                    ("Sangat tinggi - 15 Mbps", 15000), ("Maksimal / LAN - 40 Mbps", 40000) },
                () => _s.AutoBitrate && _s.Has(HostFeatures.AutoBitrate) ? 0 : _s.Kbps,
                v => { if (v == 0) _s.AutoBitrate = true; else { _s.AutoBitrate = false; _s.Kbps = v; } _s.SendSettings(true); });
            _bar.Items.Add(_qualityMenu);
            _bar.Items.Add(MakeMenu("FPS", new[] { ("30 fps", 30), ("60 fps", 60) }, () => _s.Fps, v => { _s.Fps = v; _s.SendSettings(true); }));
            _bar.Items.Add(MakeMenu("Resolusi", new[] { ("100% (asli)", 100), ("75%", 75), ("50% (paling ringan)", 50) }, () => _s.Scale, v => { _s.Scale = v; _s.SendSettings(true); }));
            _bar.Items.Add(MakeMenu("Mode gambar", new[] { ("H.264 - cepat & hemat bandwidth (disarankan)", (int)CodecId.H264), ("JPEG - tajam, butuh internet sangat kencang", (int)CodecId.Jpeg) },
                () => _s.CodecPref, v => { _s.CodecPref = (byte)v; _s.SendSettings(true); }));
            _bar.Items.Add(new ToolStripSeparator());

            var keys = new ToolStripDropDownButton("Tombol") { ToolTipText = "Kirim kombinasi tombol khusus ke PC tujuan" };
            AddKey(keys, "Task Manager (Ctrl+Shift+Esc)", 0x11, 0x10, 0x1B);
            AddKey(keys, "Tombol Windows", 0x5B);
            AddKey(keys, "Win+D  -  tampilkan desktop", 0x5B, 0x44);
            AddKey(keys, "Win+E  -  File Explorer", 0x5B, 0x45);
            AddKey(keys, "Win+R  -  Run", 0x5B, 0x52);
            AddKey(keys, "Alt+Tab", 0x12, 0x09);
            AddKey(keys, "Alt+F4  -  tutup jendela", 0x12, 0x73);
            AddKey(keys, "Ctrl+Esc  -  menu Start", 0x11, 0x1B);
            AddKey(keys, "Print Screen", 0x2C);
            keys.DropDownItems.Add(new ToolStripSeparator());
            keys.DropDownItems.Add(new ToolStripMenuItem("Ctrl+Alt+Del tidak bisa dikirim (dibatasi Windows)") { Enabled = false });
            _bar.Items.Add(keys);

            _fileBtn = Button("Kirim file", "Kirim file ke PC tujuan (atau seret file ke jendela ini). File masuk ke folder Downloads\\KilatDesk.", ChooseAndSendFiles);
            _chatBtn = Button("Chat", "Chat dengan orang di PC tujuan", OpenChat);
            _shotBtn = Button("Screenshot", "Simpan gambar layar PC tujuan ke folder Pictures\\KilatDesk", TakeScreenshot);

            var opts = new ToolStripDropDownButton("Opsi");
            _audioItem = new ToolStripMenuItem("Suara dari PC tujuan") { CheckOnClick = true, Checked = _s.Audio };
            _audioItem.CheckedChanged += (o, e) => { _s.Audio = _audioItem.Checked; _s.SendSettings(true); };
            _clipItem = new ToolStripMenuItem("Sinkron clipboard (copy-paste teks)") { CheckOnClick = true, Checked = true };
            _viewOnlyItem = new ToolStripMenuItem("Hanya lihat (mouse & keyboard tidak dikirim)") { CheckOnClick = true };
            _viewOnlyItem.CheckedChanged += (o, e) => { _s.ViewOnly = _viewOnlyItem.Checked; ReleaseKeys(); UpdateStats(); };
            _privacyItem = new ToolStripMenuItem("Layar privasi di PC tujuan (layar di sana jadi hitam)") { CheckOnClick = true };
            _privacyItem.Click += (o, e) => _s.SetPrivacy(_privacyItem.Checked);
            opts.DropDownItems.AddRange(new ToolStripItem[] { _audioItem, _clipItem, _viewOnlyItem, _privacyItem });
            _bar.Items.Add(opts);

            var full = new ToolStripButton("Layar penuh") { ToolTipText = "Ctrl+Alt+Enter untuk masuk/keluar layar penuh" };
            full.Click += (o, e) => ToggleFullscreen();
            _bar.Items.Add(full);

            var disc = new ToolStripButton("Putuskan") { Alignment = ToolStripItemAlignment.Right, Font = Ui.Bold, ForeColor = Color.FromArgb(190, 20, 20) };
            disc.Click += (o, e) => Close();
            _bar.Items.Add(disc);
            ApplyFeatures();
        }

        ToolStripButton Button(string text, string tip, Action click)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip };
            b.Click += (o, e) => click();
            _bar.Items.Add(b);
            return b;
        }

        void AddKey(ToolStripDropDownButton menu, string label, params int[] vks)
        {
            var mi = new ToolStripMenuItem(label);
            mi.Click += (o, e) => { if (!_s.ViewOnly) _s.SendCombo(vks.Select(v => (ushort)v).ToArray()); };
            menu.DropDownItems.Add(mi);
        }

        static ToolStripDropDownButton MakeMenu(string title, (string label, int value)[] items, Func<int> get, Action<int> set)
        {
            var b = new ToolStripDropDownButton(title);
            foreach (var it in items)
            {
                var mi = new ToolStripMenuItem(it.label) { Tag = it.value };
                int v = it.value;
                mi.Click += (o, e) => set(v);
                b.DropDownItems.Add(mi);
            }
            b.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem mi in b.DropDownItems) mi.Checked = (int)mi.Tag == get(); };
            return b;
        }

        /// <summary>Hides buttons for things an older (v1) host cannot do.</summary>
        void ApplyFeatures()
        {
            _fileBtn.Visible = _s.Has(HostFeatures.Files);
            _chatBtn.Visible = _s.Has(HostFeatures.Chat);
            _audioItem.Enabled = _s.Has(HostFeatures.Audio);
            _privacyItem.Enabled = _s.Has(HostFeatures.Privacy);
            _qualityMenu.DropDownItems[0].Enabled = _s.Has(HostFeatures.AutoBitrate);
        }

        void OnHostInfo()
        {
            _hostLabel.Text = "PC: " + _s.HostName;
            Text = "KilatDesk - " + _s.HostName + " (" + _s.Conn.Remote + ")";
            ApplyFeatures();
            RebuildMonitorButtons();
            if (_s.Features == 0) SetStatus("PC tujuan memakai KilatDesk versi lama: transfer file, chat, suara dan layar privasi belum tersedia.", null);
        }

        void OnHostStats()
        {
            if (_privacyItem.Checked != _s.HostPrivacy) _privacyItem.Checked = _s.HostPrivacy;
        }

        // ------------------------------------------------------------------ monitors
        int MonitorCount => _s.MonitorNames.Length;
        bool AllSupported => _s.Has(HostFeatures.AllMonitors) && MonitorCount > 1;

        string MonitorLabel(int idx) =>
            idx == Monitors.All ? "Semua monitor" : "Monitor " + (idx + 1) + (MonitorCount > 1 ? " dari " + MonitorCount : "");

        void RebuildMonitorButtons()
        {
            foreach (var b in _monButtons) _bar.Items.Remove(b);
            _monButtons.Clear();
            int pos = _bar.Items.IndexOf(_monLabel) + 1;
            for (int i = 0; i < MonitorCount; i++)
            {
                int idx = i;
                bool primary = i < _s.MonitorList.Count ? _s.MonitorList[i].Primary : _s.MonitorNames[i].Contains("utama");
                var b = new ToolStripButton((i + 1) + (primary ? " ★" : "")) { Tag = idx, ToolTipText = _s.MonitorNames[i] + "   (Ctrl+Alt+Kiri/Kanan untuk pindah)" };
                b.Click += (o, e) => { _s.RequestMonitor(idx); ShowHint("Pindah ke " + MonitorLabel(idx) + " ..."); };
                _bar.Items.Insert(pos++, b);
                _monButtons.Add(b);
            }
            if (AllSupported)
            {
                var all = new ToolStripButton("Semua") { Tag = Monitors.All, ToolTipText = "Tampilkan semua monitor sekaligus" };
                all.Click += (o, e) => { _s.RequestMonitor(Monitors.All); ShowHint("Menampilkan semua monitor ..."); };
                _bar.Items.Insert(pos, all);
                _monButtons.Add(all);
            }
            _monLabel.Visible = _monButtons.Count > 1;
            foreach (var b in _monButtons) b.Visible = _monButtons.Count > 1;
            UpdateMonitorButtons();
        }

        void UpdateMonitorButtons()
        {
            foreach (var b in _monButtons) b.Checked = (int)b.Tag == _s.Monitor;
        }

        void CycleMonitor(int dir)
        {
            var list = Enumerable.Range(0, MonitorCount).ToList();
            if (AllSupported) list.Add(Monitors.All);
            if (list.Count < 2) return;
            int i = list.IndexOf(_s.Monitor);
            int next = list[((i < 0 ? 0 : i) + dir + list.Count) % list.Count];
            _s.RequestMonitor(next);
            ShowHint("Pindah ke " + MonitorLabel(next) + " ...");
        }

        // ------------------------------------------------------------------ actions
        void ChooseAndSendFiles()
        {
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Pilih file untuk dikirim ke " + _s.HostName })
                if (dlg.ShowDialog(this) == DialogResult.OK) _s.Files.SendFiles(dlg.FileNames);
        }

        ChatForm EnsureChat()
        {
            if (_chat == null || _chat.IsDisposed)
            {
                _chat = new ChatForm(_s.HostName, "Saya", _s.HostName, t => _s.SendChat(t));
                _chat.Opened += () => _chatBtn.Text = "Chat";
            }
            return _chat;
        }

        void OpenChat() => EnsureChat().Open();

        void TakeScreenshot()
        {
            try { string p = _s.SaveScreenshot(); SetStatus("Screenshot disimpan: " + p + "   (klik untuk membuka foldernya)", p); }
            catch (Exception ex) { SetStatus("Screenshot gagal: " + ex.Message, null); }
        }
    }
}
