using System;
using System.Threading.Tasks;

namespace KilatDesk
{
    /// <summary>Fast BGRA &lt;-&gt; NV12 (full-range BT.601) conversion and bilinear scaling, multi-threaded.</summary>
    internal static unsafe class ColorConvert
    {
        static readonly byte[] Clip = BuildClip();
        static byte[] BuildClip()
        {
            var t = new byte[1024];
            for (int i = 0; i < t.Length; i++) { int v = i - 384; t[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }
            return t;
        }

        static int Chunks(int rows) => Math.Max(1, Math.Min(Environment.ProcessorCount, rows / 8));

        /// <summary>w and h must be even. dst = Y plane (dstStride*h) followed by interleaved UV plane.</summary>
        public static void BgraToNv12(byte* src, int srcStride, int w, int h, byte* dst, int dstStride)
        {
            IntPtr pSrc = (IntPtr)src, pY = (IntPtr)dst, pUV = (IntPtr)(dst + (long)dstStride * h);
            int pairs = h / 2, chunks = Chunks(pairs);
            Parallel.For(0, chunks, c =>
            {
                int start = (int)((long)pairs * c / chunks), end = (int)((long)pairs * (c + 1) / chunks);
                for (int p = start; p < end; p++)
                {
                    byte* s0 = (byte*)pSrc + (long)(p * 2) * srcStride, s1 = s0 + srcStride;
                    byte* d0 = (byte*)pY + (long)(p * 2) * dstStride, d1 = d0 + dstStride;
                    byte* uv = (byte*)pUV + (long)p * dstStride;
                    for (int x = 0; x < w; x += 2)
                    {
                        int b0 = s0[0], g0 = s0[1], r0 = s0[2], b1 = s0[4], g1 = s0[5], r1 = s0[6];
                        int b2 = s1[0], g2 = s1[1], r2 = s1[2], b3 = s1[4], g3 = s1[5], r3 = s1[6];
                        d0[0] = (byte)((77 * r0 + 150 * g0 + 29 * b0 + 128) >> 8);
                        d0[1] = (byte)((77 * r1 + 150 * g1 + 29 * b1 + 128) >> 8);
                        d1[0] = (byte)((77 * r2 + 150 * g2 + 29 * b2 + 128) >> 8);
                        d1[1] = (byte)((77 * r3 + 150 * g3 + 29 * b3 + 128) >> 8);
                        int rs = r0 + r1 + r2 + r3, gs = g0 + g1 + g2 + g3, bs = b0 + b1 + b2 + b3;
                        int u = ((-43 * rs - 85 * gs + 128 * bs + 512) >> 10) + 128;
                        int v = ((128 * rs - 107 * gs - 21 * bs + 512) >> 10) + 128;
                        uv[0] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                        uv[1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                        s0 += 8; s1 += 8; d0 += 2; d1 += 2; uv += 2;
                    }
                }
            });
        }

        /// <summary>Converts the top-left w x h area of an NV12 image into BGRA (w, h even).</summary>
        public static void Nv12ToBgra(byte* srcY, int yStride, byte* srcUV, int uvStride, int w, int h, byte* dst, int dstStride)
        {
            IntPtr pY = (IntPtr)srcY, pUV = (IntPtr)srcUV, pDst = (IntPtr)dst;
            int pairs = h / 2, chunks = Chunks(pairs);
            Parallel.For(0, chunks, c =>
            {
                fixed (byte* clip = Clip)
                {
                    byte* cl = clip + 384;
                    int start = (int)((long)pairs * c / chunks), end = (int)((long)pairs * (c + 1) / chunks);
                    for (int p = start; p < end; p++)
                    {
                        byte* y0 = (byte*)pY + (long)(p * 2) * yStride, y1 = y0 + yStride;
                        byte* uv = (byte*)pUV + (long)p * uvStride;
                        uint* o0 = (uint*)((byte*)pDst + (long)(p * 2) * dstStride), o1 = (uint*)((byte*)o0 + dstStride);
                        for (int x = 0; x < w; x += 2)
                        {
                            int u = uv[0] - 128, v = uv[1] - 128;
                            int rv = (359 * v + 128) >> 8, guv = (88 * u + 183 * v + 128) >> 8, bu = (454 * u + 128) >> 8;
                            int Y = y0[0]; o0[0] = 0xFF000000u | ((uint)cl[Y + rv] << 16) | ((uint)cl[Y - guv] << 8) | cl[Y + bu];
                            Y = y0[1]; o0[1] = 0xFF000000u | ((uint)cl[Y + rv] << 16) | ((uint)cl[Y - guv] << 8) | cl[Y + bu];
                            Y = y1[0]; o1[0] = 0xFF000000u | ((uint)cl[Y + rv] << 16) | ((uint)cl[Y - guv] << 8) | cl[Y + bu];
                            Y = y1[1]; o1[1] = 0xFF000000u | ((uint)cl[Y + rv] << 16) | ((uint)cl[Y - guv] << 8) | cl[Y + bu];
                            y0 += 2; y1 += 2; uv += 2; o0 += 2; o1 += 2;
                        }
                    }
                }
            });
        }

        /// <summary>Bilinear BGRA resize (used when the viewer asks for a lower resolution).</summary>
        public static void ScaleBgra(byte* src, int sw, int sh, int sStride, byte* dst, int dw, int dh, int dStride)
        {
            var xs = new int[dw]; var xf = new int[dw];
            for (int x = 0; x < dw; x++)
            {
                int fx = (int)(((x + 0.5) * sw / dw - 0.5) * 256);
                if (fx < 0) fx = 0;
                int ix = fx >> 8; if (ix >= sw - 1) { ix = sw - 2; fx = (ix << 8) + 255; }
                xs[x] = ix; xf[x] = fx & 255;
            }
            IntPtr pS = (IntPtr)src, pD = (IntPtr)dst;
            int chunks = Chunks(dh);
            Parallel.For(0, chunks, c =>
            {
                int start = (int)((long)dh * c / chunks), end = (int)((long)dh * (c + 1) / chunks);
                for (int y = start; y < end; y++)
                {
                    int fy = (int)(((y + 0.5) * sh / dh - 0.5) * 256);
                    if (fy < 0) fy = 0;
                    int iy = fy >> 8; if (iy >= sh - 1) { iy = sh - 2; fy = (iy << 8) + 255; }
                    int wy = fy & 255;
                    byte* r0 = (byte*)pS + (long)iy * sStride, r1 = r0 + sStride;
                    byte* o = (byte*)pD + (long)y * dStride;
                    for (int x = 0; x < dw; x++)
                    {
                        byte* a = r0 + xs[x] * 4, b = r1 + xs[x] * 4;
                        int wx = xf[x];
                        for (int k = 0; k < 3; k++)
                        {
                            int top = a[k] * (256 - wx) + a[k + 4] * wx;
                            int bot = b[k] * (256 - wx) + b[k + 4] * wx;
                            o[k] = (byte)((top * (256 - wy) + bot * wy + 32768) >> 16);
                        }
                        o[3] = 255; o += 4;
                    }
                }
            });
        }
    }
}
