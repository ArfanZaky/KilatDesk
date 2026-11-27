using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KilatDesk
{
    /// <summary>Host side: turns viewer events into real mouse/keyboard input with SendInput.</summary>
    internal sealed unsafe class InputInjector
    {
        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint MOVE = 0x1, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, WHEEL = 0x800, HWHEEL = 0x1000;
        const uint KEYUP = 0x2, EXTENDED = 0x1, SCANCODE = 0x8;
        static readonly uint[] Down = { 0x2, 0x8, 0x20, 0x80, 0x80 }, Up = { 0x4, 0x10, 0x40, 0x100, 0x100 };

        volatile MonitorInfo _mon;
        readonly object _gate = new object();
        readonly HashSet<ushort> _keys = new HashSet<ushort>();
        readonly HashSet<byte> _buttons = new HashSet<byte>();
        readonly Dictionary<ushort, ushort> _keyScan = new Dictionary<ushort, ushort>();
        readonly Dictionary<ushort, bool> _keyExt = new Dictionary<ushort, bool>();

        public void SetMonitor(MonitorInfo m) => _mon = m;

        static void Send(ref INPUT inp)
        {
            fixed (INPUT* p = &inp) Native.SendInput(1, p, Marshal.SizeOf(typeof(INPUT)));
        }

        public void MouseMove(ushort nx, ushort ny)
        {
            var m = _mon;
            if (m == null) return;
            int px = m.X + (int)(((long)nx * (m.Width - 1) + 32767) / 65535);
            int py = m.Y + (int)(((long)ny * (m.Height - 1) + 32767) / 65535);
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
            int vw = Math.Max(2, Native.GetSystemMetrics(78)), vh = Math.Max(2, Native.GetSystemMetrics(79));
            var inp = new INPUT { type = INPUT_MOUSE };
            // Windows truncates when mapping 0..65535 back to pixels, so round up to land on the exact pixel
            inp.u.mi.dx = (int)(((long)(px - vx) * 65535 + (vw - 2)) / (vw - 1));
            inp.u.mi.dy = (int)(((long)(py - vy) * 65535 + (vh - 2)) / (vh - 1));
            inp.u.mi.dwFlags = MOVE | ABSOLUTE | VIRTUALDESK;
            Send(ref inp);
        }

        /// <param name="button">0 left, 1 right, 2 middle, 3 X1, 4 X2</param>
        public void MouseButton(byte button, bool down)
        {
            if (button > 4) return;
            lock (_gate) { if (down) _buttons.Add(button); else _buttons.Remove(button); }
            var inp = new INPUT { type = INPUT_MOUSE };
            inp.u.mi.dwFlags = down ? Down[button] : Up[button];
            if (button >= 3) inp.u.mi.mouseData = (uint)(button == 3 ? 1 : 2);
            Send(ref inp);
        }

        public void Wheel(short delta, bool horizontal)
        {
            var inp = new INPUT { type = INPUT_MOUSE };
            inp.u.mi.dwFlags = horizontal ? HWHEEL : WHEEL;
            inp.u.mi.mouseData = unchecked((uint)(int)delta);
            Send(ref inp);
        }

        /// <param name="flags">bit0 = key up, bit1 = extended key</param>
        public void Key(ushort vk, ushort scan, byte flags)
        {
            bool up = (flags & 1) != 0, ext = (flags & 2) != 0;
            lock (_gate)
            {
                if (up) _keys.Remove(vk);
                else { _keys.Add(vk); _keyScan[vk] = scan; _keyExt[vk] = ext; }
            }
            var inp = new INPUT { type = INPUT_KEYBOARD };
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = scan;
            inp.u.ki.dwFlags = (up ? KEYUP : 0) | (ext ? EXTENDED : 0) | (vk == 0 ? SCANCODE : 0);
            Send(ref inp);
        }

        /// <summary>Releases everything the viewer still holds (on focus loss or disconnect) so nothing gets stuck.</summary>
        public void ReleaseAll()
        {
            ushort[] keys; byte[] buttons;
            lock (_gate)
            {
                keys = new ushort[_keys.Count]; _keys.CopyTo(keys);
                buttons = new byte[_buttons.Count]; _buttons.CopyTo(buttons);
            }
            foreach (var k in keys)
                Key(k, _keyScan.TryGetValue(k, out var s) ? s : (ushort)0, (byte)(1 | (_keyExt.TryGetValue(k, out var e) && e ? 2 : 0)));
            foreach (var b in buttons) MouseButton(b, false);
        }
    }

    /// <summary>Maps the host's current mouse cursor to a small id the viewer can show locally (no lag).</summary>
    internal static class CursorShapes
    {
        static readonly int[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
        static IntPtr[] _handles;
        public const byte Hidden = 255;

        public static byte Current()
        {
            if (_handles == null)
            {
                var h = new IntPtr[Ids.Length];
                for (int i = 0; i < Ids.Length; i++) h[i] = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Ids[i]);
                _handles = h;
            }
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!Native.GetCursorInfo(ref ci)) return 0;
            if ((ci.flags & 1) == 0) return Hidden;
            int idx = Array.IndexOf(_handles, ci.hCursor);
            return (byte)(idx < 0 ? 0 : idx);
        }

        public static Cursor ToCursor(byte id)
        {
            switch (id)
            {
                case 1: return Cursors.IBeam;
                case 2: return Cursors.WaitCursor;
                case 3: return Cursors.Cross;
                case 4: return Cursors.UpArrow;
                case 5: return Cursors.SizeNWSE;
                case 6: return Cursors.SizeNESW;
                case 7: return Cursors.SizeWE;
                case 8: return Cursors.SizeNS;
                case 9: return Cursors.SizeAll;
                case 10: return Cursors.No;
                case 11: return Cursors.Hand;
                case 12: return Cursors.AppStarting;
                case 13: return Cursors.Help;
                case Hidden: return Cursors.Cross;
                default: return Cursors.Default;
            }
        }
    }

    /// <summary>
    /// Viewer side: low-level keyboard hook so Win, Alt+Tab, Alt+F4 etc. go to the remote PC instead of this one.
    /// The handler returns true to swallow the key locally. Injected keys are ignored (avoids loops on one PC).
    /// </summary>
    internal sealed unsafe class KeyboardHook : IDisposable
    {
        public delegate bool KeyHandler(uint vk, uint scan, bool extended, bool up);
        readonly LowLevelProc _proc;
        readonly KeyHandler _handler;
        IntPtr _hook;

        public KeyboardHook(KeyHandler handler)
        {
            _handler = handler;
            _proc = Proc;
            _hook = Native.SetWindowsHookExW(13, _proc, Native.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) Log.Warn("Keyboard hook gagal: " + Marshal.GetLastWin32Error());
        }

        public bool Installed => _hook != IntPtr.Zero;

        IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT*)lParam;
                if ((k->flags & 0x10) == 0) // LLKHF_INJECTED
                {
                    int msg = (int)wParam;
                    bool up = msg == 0x101 || msg == 0x105;
                    try { if (_handler(k->vkCode, k->scanCode, (k->flags & 1) != 0, up)) return (IntPtr)1; }
                    catch (Exception ex) { Log.Error("hook", ex); }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }
}
