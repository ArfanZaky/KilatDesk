using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace KilatDesk
{
    internal static class CodecId
    {
        public const byte H264 = 1, Jpeg = 2;
        public static string Name(byte c) => c == H264 ? "H.264" : "JPEG";
    }

    internal unsafe interface IFrameEncoder : IDisposable
    {
        string Name { get; }
        byte Codec { get; }
        /// <summary>Encodes a BGRA frame of the encoder's size. Packets are delivered through the creation callback.</summary>
        bool Encode(byte* bgra, int stride, long time100ns, bool forceKey);
        void SetBitrate(int kbps);
    }

    internal unsafe interface IFrameDecoder : IDisposable
    {
        /// <summary>Decodes a packet into the BGRA frame buffer. Returns true when the picture changed.</summary>
        bool Decode(byte[] data, int offset, int count, byte* fb, int fbStride);
    }

    internal static class Codecs
    {
        public static IFrameEncoder CreateEncoder(byte codec, int w, int h, int fps, int kbps, Action<byte[], bool> onPacket)
        {
            if (codec == CodecId.H264)
            {
                try { return new H264FrameEncoder(w, h, fps, kbps, onPacket); }
                catch (Exception ex) { Log.Warn("H.264 encoder gagal, pakai JPEG: " + ex.Message); }
            }
            return new JpegFrameEncoder(w, h, kbps, onPacket);
        }

        public static IFrameDecoder CreateDecoder(byte codec, int w, int h) =>
            codec == CodecId.H264 ? (IFrameDecoder)new H264FrameDecoder(w, h) : new JpegFrameDecoder(w, h);
    }

    // ------------------------------------------------------------------ H.264
    internal sealed unsafe class H264FrameEncoder : IFrameEncoder
    {
        readonly H264Encoder _enc;
        readonly int _w, _h;
        IntPtr _nv12;
        public string Name => _enc.Name;
        public byte Codec => CodecId.H264;

        public H264FrameEncoder(int w, int h, int fps, int kbps, Action<byte[], bool> onPacket)
        {
            _w = w; _h = h;
            _enc = H264Encoder.Create(w, h, fps, kbps, true, onPacket);
            _nv12 = Marshal.AllocHGlobal(w * h * 3 / 2);
        }

        public bool Encode(byte* bgra, int stride, long time100ns, bool forceKey)
        {
            ColorConvert.BgraToNv12(bgra, stride, _w, _h, (byte*)_nv12, _w);
            return _enc.Encode((byte*)_nv12, time100ns, forceKey);
        }

        public void SetBitrate(int kbps) => _enc.SetBitrate(kbps);

        public void Dispose()
        {
            _enc.Dispose();
            if (_nv12 != IntPtr.Zero) { Marshal.FreeHGlobal(_nv12); _nv12 = IntPtr.Zero; }
        }
    }

    internal sealed unsafe class H264FrameDecoder : IFrameDecoder
    {
        readonly H264Decoder _dec;
        readonly int _w, _h;
        readonly Nv12FrameHandler _handler;
        byte* _fb; int _fbStride; bool _got;

        public H264FrameDecoder(int w, int h) { _w = w; _h = h; _dec = new H264Decoder(w, h); _handler = OnFrame; }

        void OnFrame(byte* y, byte* uv, int stride, int width, int height)
        {
            ColorConvert.Nv12ToBgra(y, stride, uv, stride, Math.Min(_w, width) & ~1, Math.Min(_h, height) & ~1, _fb, _fbStride);
            _got = true;
        }

        public bool Decode(byte[] data, int offset, int count, byte* fb, int fbStride)
        {
            _fb = fb; _fbStride = fbStride; _got = false;
            _dec.Decode(data, offset, count, _handler);
            return _got;
        }

        public void Dispose() => _dec.Dispose();
    }

    // ------------------------------------------------------------------ JPEG tiles (fallback / "sharp" mode)
    internal sealed unsafe class JpegFrameEncoder : IFrameEncoder
    {
        const int T = 64;
        readonly int _w, _h;
        readonly Action<byte[], bool> _onPacket;
        readonly ImageCodecInfo _jpeg;
        IntPtr _prev;
        bool _hasPrev;
        long _quality = 70;
        public string Name => "JPEG tiles (CPU)";
        public byte Codec => CodecId.Jpeg;

        public JpegFrameEncoder(int w, int h, int kbps, Action<byte[], bool> onPacket)
        {
            _w = w; _h = h; _onPacket = onPacket;
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") _jpeg = c;
            _prev = Marshal.AllocHGlobal(w * h * 4);
            SetBitrate(kbps);
        }

        public void SetBitrate(int kbps) => _quality = kbps >= 20000 ? 90 : kbps >= 10000 ? 82 : kbps >= 5000 ? 72 : 55;

        bool TileChanged(byte* cur, int stride, int x0, int y0, int tw, int th)
        {
            byte* prev = (byte*)_prev;
            for (int r = 0; r < th; r++)
                if (Native.memcmp(cur + (long)(y0 + r) * stride + x0 * 4, prev + ((long)(y0 + r) * _w + x0) * 4, (UIntPtr)(uint)(tw * 4)) != 0)
                    return true;
            return false;
        }

        public bool Encode(byte* bgra, int stride, long time100ns, bool forceKey)
        {
            bool full = forceKey || !_hasPrev;
            int cols = (_w + T - 1) / T, rows = (_h + T - 1) / T, count = 0;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write((ushort)0);
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _quality);
                for (int ty = 0; ty < rows; ty++)
                {
                    int y0 = ty * T, th = Math.Min(T, _h - y0);
                    int tx = 0;
                    while (tx < cols)
                    {
                        if (!full && !TileChanged(bgra, stride, tx * T, y0, Math.Min(T, _w - tx * T), th)) { tx++; continue; }
                        int start = tx;
                        while (tx < cols && (full || TileChanged(bgra, stride, tx * T, y0, Math.Min(T, _w - tx * T), th))) tx++;
                        int x0 = start * T, rw = Math.Min(tx * T, _w) - x0;
                        byte[] jpg;
                        using (var bmp = new Bitmap(rw, th, stride, PixelFormat.Format32bppRgb, (IntPtr)(bgra + (long)y0 * stride + x0 * 4)))
                        using (var js = new MemoryStream())
                        {
                            bmp.Save(js, _jpeg, ep);
                            jpg = js.ToArray();
                        }
                        bw.Write((ushort)x0); bw.Write((ushort)y0); bw.Write((ushort)rw); bw.Write((ushort)th);
                        bw.Write(jpg.Length); bw.Write(jpg);
                        for (int r = 0; r < th; r++)
                            Buffer.MemoryCopy(bgra + (long)(y0 + r) * stride + x0 * 4, (byte*)_prev + ((long)(y0 + r) * _w + x0) * 4, rw * 4, rw * 4);
                        count++;
                    }
                }
            }
            _hasPrev = true;
            if (count == 0) return true;
            var pkt = ms.ToArray();
            pkt[0] = (byte)count; pkt[1] = (byte)(count >> 8);
            _onPacket(pkt, full);
            return true;
        }

        public void Dispose()
        {
            if (_prev != IntPtr.Zero) { Marshal.FreeHGlobal(_prev); _prev = IntPtr.Zero; }
        }
    }

    internal sealed unsafe class JpegFrameDecoder : IFrameDecoder
    {
        readonly int _w, _h;
        public JpegFrameDecoder(int w, int h) { _w = w; _h = h; }

        public bool Decode(byte[] d, int offset, int count, byte* fb, int fbStride)
        {
            int n = d[offset] | (d[offset + 1] << 8), p = offset + 2, end = offset + count;
            for (int i = 0; i < n && p + 12 <= end; i++)
            {
                int x = BitConverter.ToUInt16(d, p), y = BitConverter.ToUInt16(d, p + 2);
                int w = BitConverter.ToUInt16(d, p + 4), h = BitConverter.ToUInt16(d, p + 6);
                int len = BitConverter.ToInt32(d, p + 8);
                p += 12;
                if (len <= 0 || p + len > end) break;
                using (var ms = new MemoryStream(d, p, len))
                using (var bmp = new Bitmap(ms))
                {
                    int cw = Math.Min(Math.Min(w, bmp.Width), _w - x), ch = Math.Min(Math.Min(h, bmp.Height), _h - y);
                    if (cw > 0 && ch > 0)
                    {
                        var bd = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        for (int r = 0; r < ch; r++)
                            Buffer.MemoryCopy((byte*)bd.Scan0 + (long)r * bd.Stride, fb + (long)(y + r) * fbStride + x * 4, cw * 4, cw * 4);
                        bmp.UnlockBits(bd);
                    }
                }
                p += len;
            }
            return n > 0;
        }

        public void Dispose() { }
    }
}
