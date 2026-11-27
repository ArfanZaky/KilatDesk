using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KilatDesk
{
    internal sealed class MonitorInfo
    {
        public string Name;
        public int X, Y, Width, Height;
        public bool Primary;
        public int Adapter = -1, Output = -1; // DXGI location (-1 = GDI only)
        public override string ToString() => Name + " (" + Width + "x" + Height + ")" + (Primary ? " - utama" : "");
        public bool SameGeometry(MonitorInfo o) => o != null && X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    }

    internal enum CaptureResult { NoChange, Changed, Error }

    internal interface IScreenCapture : IDisposable
    {
        string Kind { get; }
        int Width { get; }
        int Height { get; }
        IntPtr Pixels { get; }   // BGRA, top-down
        int Stride { get; }
        CaptureResult Capture(int timeoutMs);
    }

    internal static class Monitors
    {
        /// <summary>Index used in the protocol for "all monitors as one picture".</summary>
        public const int All = 1000;

        public static List<MonitorInfo> Enumerate()
        {
            List<MonitorInfo> list = null;
            try { list = DxgiCapture.EnumerateOutputs(); } catch (Exception ex) { Log.Warn("DXGI enum: " + ex.Message); }
            if (list == null || list.Count == 0)
            {
                list = new List<MonitorInfo>();
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                    list.Add(new MonitorInfo { X = s.Bounds.X, Y = s.Bounds.Y, Width = s.Bounds.Width, Height = s.Bounds.Height, Primary = s.Primary });
            }
            // stable order: primary first, then left-to-right, top-to-bottom
            list = list.OrderByDescending(m => m.Primary).ThenBy(m => m.X).ThenBy(m => m.Y).ToList();
            for (int i = 0; i < list.Count; i++) list[i].Name = "Monitor " + (i + 1);
            return list;
        }

        public static bool SameLayout(List<MonitorInfo> a, List<MonitorInfo> b) =>
            a != null && b != null && a.Count == b.Count && a.Zip(b, (x, y) => x.SameGeometry(y)).All(v => v);

        /// <summary>Bounding box of all monitors (virtual desktop).</summary>
        public static MonitorInfo Bounds(List<MonitorInfo> list)
        {
            int x0 = list.Min(m => m.X), y0 = list.Min(m => m.Y);
            int x1 = list.Max(m => m.X + m.Width), y1 = list.Max(m => m.Y + m.Height);
            return new MonitorInfo { Name = "Semua monitor", X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
        }

        public static IScreenCapture Open(MonitorInfo m)
        {
            if (m.Adapter >= 0)
            {
                try { return new DxgiCapture(m); }
                catch (Exception ex) { Log.Warn("DXGI capture tidak tersedia untuk " + m + ", pakai GDI: " + ex.Message); }
            }
            return new GdiCapture(m);
        }
    }

    // ------------------------------------------------------------------ DXGI Desktop Duplication
    internal sealed unsafe class DxgiCapture : IScreenCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        struct OUTPUT_DESC { public fixed ushort DeviceName[32]; public RECT Desktop; public int Attached; public int Rotation; public IntPtr Monitor; }
        [StructLayout(LayoutKind.Sequential)]
        struct OUTDUPL_DESC { public uint Width, Height, RefNum, RefDen, Format, Scanline, Scaling; public int Rotation; public int InSysMem; }
        [StructLayout(LayoutKind.Sequential)]
        struct FRAME_INFO { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, Protected; public int PtrX, PtrY, PtrVisible; public uint MetaSize, PtrShapeSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct TEX2D_DESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

        static readonly Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_Tex2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        const int WAIT_TIMEOUT = unchecked((int)0x887A0027), ACCESS_LOST = unchecked((int)0x887A0026);

        readonly MonitorInfo _mon;
        IntPtr _device, _context, _output1, _dupl, _staging, _buffer;
        int _w, _h, _texW, _texH, _rot, _bufSize;
        bool _needFirst = true;
        long _lastReinit;
        int _dupStart;

        /// <summary>DXGI rotation: 0/1 none, 2 = 90°, 3 = 180°, 4 = 270°.</summary>
        public int Rotation => _rot;
        /// <summary>Diagnostics: frame info of the last acquired frame.</summary>
        public long LastPresent; public uint LastAccumulated;
        public string Kind => _rot > 1 ? "DXGI (diputar)" : "DXGI";
        public int Width => _w;
        public int Height => _h;
        public IntPtr Pixels => _buffer;
        public int Stride => _w * 4;

        public static List<MonitorInfo> EnumerateOutputs()
        {
            var list = new List<MonitorInfo>();
            Guid iid = IID_Factory1;
            Com.Check(Native.CreateDXGIFactory1(ref iid, out IntPtr factory), "CreateDXGIFactory1");
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(factory, 12))(factory, a, &adapter) < 0) break;
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(adapter, 7))(adapter, o, &output) < 0) break;
                        OUTPUT_DESC d;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, OUTPUT_DESC*, int>)Com.Vt(output, 7))(output, &d) >= 0 && d.Attached != 0)
                        {
                            var m = new MonitorInfo
                            {
                                X = d.Desktop.Left, Y = d.Desktop.Top,
                                Width = d.Desktop.Right - d.Desktop.Left, Height = d.Desktop.Bottom - d.Desktop.Top,
                                Adapter = (int)a, Output = (int)o
                            };
                            m.Primary = m.X == 0 && m.Y == 0;
                            list.Add(m);
                        }
                        Com.Release(output);
                    }
                    Com.Release(adapter);
                }
            }
            finally { Com.Release(factory); }
            return list;
        }

        public DxgiCapture(MonitorInfo m)
        {
            _mon = m;
            try { Init(); }
            catch { Dispose(); throw; }
        }

        void Init()
        {
            Guid iid = IID_Factory1;
            Com.Check(Native.CreateDXGIFactory1(ref iid, out IntPtr factory), "CreateDXGIFactory1");
            IntPtr adapter = IntPtr.Zero, output = IntPtr.Zero;
            try
            {
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(factory, 12))(factory, (uint)_mon.Adapter, &adapter), "EnumAdapters1");
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(adapter, 7))(adapter, (uint)_mon.Output, &output), "EnumOutputs");
                Com.Check(Com.QI(output, IID_Output1, out _output1), "QI IDXGIOutput1");
                Com.Check(Native.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out _device, out _, out _context), "D3D11CreateDevice");
            }
            finally { Com.Release(output); Com.Release(adapter); Com.Release(factory); }
            Duplicate();
        }

        void Duplicate()
        {
            IntPtr dupl;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Com.Vt(_output1, 22))(_output1, _device, &dupl), "DuplicateOutput");
            _dupl = dupl;
            OUTDUPL_DESC d;
            ((delegate* unmanaged[Stdcall]<IntPtr, OUTDUPL_DESC*, void>)Com.Vt(_dupl, 7))(_dupl, &d);
            _rot = d.Rotation;
            int mw = (int)d.Width, mh = (int)d.Height;
            bool swap = _rot == 2 || _rot == 4;
            // the desktop image is delivered in the panel's native orientation; find the desktop (rotated) size
            if (swap && !(mw == _mon.Width && mh == _mon.Height)) SetSize(mh, mw);
            else SetSize(mw, mh);
            _needFirst = true;
            _dupStart = Environment.TickCount;
        }

        void SetSize(int w, int h)
        {
            if (w == _w && h == _h && _buffer != IntPtr.Zero) return;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufSize = w * h * 4;
            _buffer = Marshal.AllocHGlobal(_bufSize);
            _w = w; _h = h;
        }

        /// <summary>(Re)creates the CPU-readable staging texture to match the acquired desktop texture.</summary>
        void EnsureStaging(IntPtr tex)
        {
            TEX2D_DESC td;
            ((delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, void>)Com.Vt(tex, 10))(tex, &td);
            if (td.Format != 87 && td.Format != 91) throw new NotSupportedException("format desktop " + td.Format + " (HDR/10-bit)");
            if (_staging == IntPtr.Zero || td.Width != _texW || td.Height != _texH)
            {
                Com.Release(ref _staging);
                var sd = new TEX2D_DESC { Width = td.Width, Height = td.Height, MipLevels = 1, ArraySize = 1, Format = td.Format, SampleCount = 1, Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/ };
                IntPtr st;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, TEX2D_DESC*, IntPtr, IntPtr*, int>)Com.Vt(_device, 5))(_device, &sd, IntPtr.Zero, &st), "CreateTexture2D");
                _staging = st;
                _texW = (int)td.Width; _texH = (int)td.Height;
            }
            bool swap = _rot == 2 || _rot == 4;
            SetSize(swap ? _texH : _texW, swap ? _texW : _texH); // a size change makes the host reconfigure the encoder
        }

        public CaptureResult Capture(int timeoutMs)
        {
            if (_dupl == IntPtr.Zero)
            {
                long now = Environment.TickCount;
                if (now - _lastReinit < 250) { if (timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, 50)); return CaptureResult.NoChange; }
                _lastReinit = now;
                try { Duplicate(); }
                catch { return CaptureResult.NoChange; } // e.g. UAC / lock screen: try again later
            }
            FRAME_INFO fi;
            IntPtr res = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FRAME_INFO*, IntPtr*, int>)Com.Vt(_dupl, 8))(_dupl, (uint)Math.Max(0, timeoutMs), &fi, &res);
            if (hr == WAIT_TIMEOUT) return CaptureResult.NoChange;
            if (hr < 0)
            {
                Com.Release(ref _dupl);
                return hr == ACCESS_LOST ? CaptureResult.NoChange : CaptureResult.Error;
            }
            IntPtr tex = IntPtr.Zero;
            LastPresent = fi.LastPresentTime; LastAccumulated = fi.AccumulatedFrames;
            try
            {
                if (fi.LastPresentTime == 0 && !_needFirst) return CaptureResult.NoChange; // only the mouse moved
                // The first frame of a new duplication is an empty (black) placeholder; the real desktop image
                // arrives on the next acquire a few ms later — skip it (but never wait forever).
                if (_needFirst && fi.LastPresentTime == 0 && fi.AccumulatedFrames == 0 && Environment.TickCount - _dupStart < 500)
                    return CaptureResult.NoChange;
                if (Com.QI(res, IID_Tex2D, out tex) < 0) return CaptureResult.Error;
                EnsureStaging(tex);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Com.Vt(_context, 47))(_context, _staging, tex);
                MAPPED map;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MAPPED*, int>)Com.Vt(_context, 14))(_context, _staging, 0, 1, 0, &map) < 0)
                    return CaptureResult.Error;
                byte* s = (byte*)map.pData, d = (byte*)_buffer;
                if (_rot <= 1)
                {
                    int row = Math.Min(_w, _texW) * 4, rows = Math.Min(_h, _texH);
                    if (map.RowPitch == row && row == _w * 4) Buffer.MemoryCopy(s, d, _bufSize, (long)row * rows);
                    else for (int y = 0; y < rows; y++) Buffer.MemoryCopy(s + (long)y * map.RowPitch, d + (long)y * _w * 4, row, row);
                }
                else RotateCopy(s, (int)map.RowPitch, d, _w, _h, _rot);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Com.Vt(_context, 15))(_context, _staging, 0);
                _needFirst = false;
                return CaptureResult.Changed;
            }
            finally
            {
                Com.Release(tex);
                Com.Release(res);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(_dupl, 14))(_dupl);
            }
        }

        /// <summary>Turns the panel-oriented texture into the desktop orientation the user sees.</summary>
        static void RotateCopy(byte* src, int pitch, byte* dst, int w, int h, int rot)
        {
            IntPtr ps = (IntPtr)src, pd = (IntPtr)dst;
            int chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, h / 16));
            Parallel.For(0, chunks, c =>
            {
                byte* s = (byte*)ps;
                int y0 = (int)((long)h * c / chunks), y1 = (int)((long)h * (c + 1) / chunks);
                for (int dy = y0; dy < y1; dy++)
                {
                    uint* o = (uint*)((byte*)pd + (long)dy * w * 4);
                    if (rot == 2) { int tx = dy * 4; for (int dx = 0; dx < w; dx++) o[dx] = *(uint*)(s + (long)(w - 1 - dx) * pitch + tx); }
                    else if (rot == 4) { int tx = (h - 1 - dy) * 4; for (int dx = 0; dx < w; dx++) o[dx] = *(uint*)(s + (long)dx * pitch + tx); }
                    else { uint* r = (uint*)(s + (long)(h - 1 - dy) * pitch); for (int dx = 0; dx < w; dx++) o[dx] = r[w - 1 - dx]; }
                }
            });
        }

        public void Dispose()
        {
            Com.Release(ref _dupl); Com.Release(ref _staging); Com.Release(ref _output1);
            Com.Release(ref _context); Com.Release(ref _device);
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ all monitors as one picture
    internal sealed unsafe class MultiCapture : IScreenCapture
    {
        readonly List<MonitorInfo> _mons;
        readonly List<IScreenCapture> _caps = new List<IScreenCapture>();
        readonly MonitorInfo _bounds;
        IntPtr _buf;
        bool _first = true;

        public MonitorInfo BoundsInfo => _bounds;
        public string Kind => "semua monitor: " + string.Join("+", _caps.Select(c => c.Kind));
        public int Width => _bounds.Width;
        public int Height => _bounds.Height;
        public IntPtr Pixels => _buf;
        public int Stride => _bounds.Width * 4;

        public MultiCapture(List<MonitorInfo> mons)
        {
            _mons = mons;
            _bounds = Monitors.Bounds(mons);
            long n = (long)_bounds.Width * _bounds.Height;
            _buf = Marshal.AllocHGlobal((IntPtr)(n * 4));
            uint* p = (uint*)_buf;
            for (long i = 0; i < n; i++) p[i] = 0xFF000000; // gaps between monitors stay black
            try { foreach (var m in mons) _caps.Add(Monitors.Open(m)); }
            catch { Dispose(); throw; }
        }

        public CaptureResult Capture(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                bool any = false;
                for (int i = 0; i < _caps.Count; i++)
                {
                    var c = _caps[i]; var m = _mons[i];
                    var r = c.Capture(0);
                    if (r == CaptureResult.Error) return CaptureResult.Error;
                    if (r != CaptureResult.Changed) continue;
                    if (c.Width != m.Width || c.Height != m.Height) return CaptureResult.Error; // layout changed -> host reconfigures
                    int ox = m.X - _bounds.X, oy = m.Y - _bounds.Y, row = c.Width * 4;
                    byte* src = (byte*)c.Pixels, dst = (byte*)_buf;
                    for (int y = 0; y < c.Height; y++)
                        Buffer.MemoryCopy(src + (long)y * c.Stride, dst + ((long)(oy + y) * _bounds.Width + ox) * 4, row, row);
                    any = true;
                }
                if (any) { _first = false; return CaptureResult.Changed; }
                if (!_first && sw.ElapsedMilliseconds >= timeoutMs) return CaptureResult.NoChange;
                if (sw.ElapsedMilliseconds > Math.Max(timeoutMs, 2000)) return CaptureResult.NoChange;
                Thread.Sleep(3);
            }
        }

        public void Dispose()
        {
            foreach (var c in _caps) { try { c.Dispose(); } catch { } }
            _caps.Clear();
            if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ GDI fallback
    internal sealed unsafe class GdiCapture : IScreenCapture
    {
        readonly MonitorInfo _m;
        IntPtr _screenDC, _memDC, _dib, _old, _bits, _prev;
        readonly int _size;
        bool _first = true;

        public string Kind => "GDI";
        public int Width => _m.Width;
        public int Height => _m.Height;
        public IntPtr Pixels => _bits;
        public int Stride => _m.Width * 4;

        public GdiCapture(MonitorInfo m)
        {
            _m = m;
            _screenDC = Native.GetDC(IntPtr.Zero);
            _memDC = Native.CreateCompatibleDC(_screenDC);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = m.Width, biHeight = -m.Height, biPlanes = 1, biBitCount = 32 };
            _dib = Native.CreateDIBSection(_screenDC, ref bi, 0, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection gagal");
            _old = Native.SelectObject(_memDC, _dib);
            _size = m.Width * m.Height * 4;
            _prev = Marshal.AllocHGlobal(_size);
        }

        public CaptureResult Capture(int timeoutMs)
        {
            if (!Native.BitBlt(_memDC, 0, 0, _m.Width, _m.Height, _screenDC, _m.X, _m.Y, Native.SRCCOPY))
                return CaptureResult.Error;
            if (!_first && Native.memcmp((byte*)_bits, (byte*)_prev, (UIntPtr)(uint)_size) == 0)
            {
                if (timeoutMs > 0) Thread.Sleep(Math.Max(1, Math.Min(timeoutMs, 15)));
                return CaptureResult.NoChange;
            }
            Buffer.MemoryCopy((void*)_bits, (void*)_prev, _size, _size);
            _first = false;
            return CaptureResult.Changed;
        }

        public void Dispose()
        {
            if (_memDC != IntPtr.Zero) { Native.SelectObject(_memDC, _old); Native.DeleteDC(_memDC); _memDC = IntPtr.Zero; }
            if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
            if (_screenDC != IntPtr.Zero) { Native.ReleaseDC(IntPtr.Zero, _screenDC); _screenDC = IntPtr.Zero; }
            if (_prev != IntPtr.Zero) { Marshal.FreeHGlobal(_prev); _prev = IntPtr.Zero; }
        }
    }
}
