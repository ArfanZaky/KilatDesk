using System;
using System.Runtime.InteropServices;
using System.Security;

namespace KilatDesk
{
    /// <summary>Raw COM helpers: we call vtable slots directly through function pointers.</summary>
    internal static unsafe class Com
    {
        public static void* Vt(IntPtr p, int slot) => (*(void***)p)[slot];

        public static uint Release(IntPtr p) =>
            p == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(p, 2))(p);

        public static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Release(p); p = IntPtr.Zero; }
        }

        public static int QI(IntPtr p, Guid iid, out IntPtr ppv)
        {
            IntPtr r = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vt(p, 0))(p, &iid, &r);
            ppv = r;
            return hr;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new ComException(hr, what);
        }
    }

    internal sealed class ComException : Exception
    {
        public ComException(int hr, string what) : base(what + " gagal (HRESULT 0x" + hr.ToString("X8") + ")") { HResult = hr; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    internal delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [SuppressUnmanagedCodeSecurity]
    internal static unsafe class Native
    {
        // ---- user32 ----
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT* inputs, int cb);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr hInst, IntPtr id);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

        // ---- gdi32 ----
        [DllImport("gdi32.dll")] public static extern int StretchDIBits(IntPtr hdc, int xDst, int yDst, int wDst, int hDst, int xSrc, int ySrc, int wSrc, int hSrc, void* bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern int PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

        // ---- kernel32 / winmm / msvcrt ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int memcmp(byte* a, byte* b, UIntPtr n);

        // ---- ole32 ----
        [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, uint coinit);
        [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
        [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

        // ---- d3d11 / dxgi ----
        [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

        // ---- mfplat ----
        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr mt);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLen, out IntPtr buffer);
        [DllImport("mfplat.dll")] public static extern int MFTEnumEx(Guid category, uint flags, ref MFT_REGISTER_TYPE_INFO input, ref MFT_REGISTER_TYPE_INFO output, out IntPtr activates, out uint count);

        public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, BLACKNESS = 0x00000042;

        /// <summary>Make sure the calling thread is in the COM multithreaded apartment.</summary>
        public static void EnsureMta() { CoInitializeEx(IntPtr.Zero, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }
}
