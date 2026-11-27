using System;

namespace KilatDesk
{
    internal unsafe delegate void Nv12FrameHandler(byte* y, byte* uv, int stride, int width, int height);

    /// <summary>Microsoft H.264 decoder MFT in low-latency mode (no frame reordering delay).</summary>
    internal sealed unsafe class H264Decoder : IDisposable
    {
        IntPtr _mft, _outSample, _outBuffer;
        bool _provides;
        int _outW, _outH, _stride, _outSize;
        long _t;

        public H264Decoder(int w, int h)
        {
            MF.Startup();
            Guid clsid = MF.CLSID_H264Decoder, iid = MF.IID_IMFTransform;
            Com.Check(Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out _mft), "CoCreateInstance(H264 decoder)");
            try
            {
                if (MF.GetAttributes(_mft, out IntPtr attrs) >= 0 && attrs != IntPtr.Zero) { MF.SetU32(attrs, MF.LOW_LATENCY, 1); Com.Release(attrs); }
                if (Com.QI(_mft, MF.IID_ICodecAPI, out IntPtr api) >= 0) { MF.CodecSetBool(api, MF.API_LowLatency, true); Com.Release(api); }
                IntPtr it = MF.CreateVideoType(MF.Format_H264, w, h, 60);
                int hr = MF.SetInputType(_mft, it);
                Com.Release(it);
                Com.Check(hr, "SetInputType(H264)");
                SetupOutput();
                MF.ProcessMessage(_mft, MF.MSG_BEGIN_STREAMING);
                MF.ProcessMessage(_mft, MF.MSG_START_OF_STREAM);
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Checks whether H.264 decoding is available on this PC (Windows "N" editions may lack it).</summary>
        public static bool IsAvailable()
        {
            try { using (new H264Decoder(640, 360)) return true; }
            catch (Exception ex) { Log.Warn("H.264 decoder tidak tersedia: " + ex.Message); return false; }
        }

        void SetupOutput()
        {
            int hr = -1;
            for (uint i = 0; ; i++)
            {
                if (MF.GetOutputAvailableType(_mft, i, out IntPtr t) < 0) break;
                bool nv12 = MF.GetGuid(t, MF.MT_SUBTYPE, out Guid st) >= 0 && st == MF.Format_NV12;
                if (nv12) hr = MF.SetOutputType(_mft, t);
                Com.Release(t);
                if (nv12) break;
            }
            Com.Check(hr, "SetOutputType(NV12)");
            if (MF.GetOutputCurrentType(_mft, out IntPtr cur) >= 0)
            {
                if (MF.GetU64(cur, MF.MT_FRAME_SIZE, out ulong fs) >= 0) { _outW = (int)(fs >> 32); _outH = (int)(fs & 0xFFFFFFFF); }
                _stride = MF.GetU32(cur, MF.MT_DEFAULT_STRIDE, out uint ds) >= 0 && (int)ds >= _outW ? (int)ds : _outW;
                Com.Release(cur);
            }
            Com.Check(MF.GetOutputStreamInfo(_mft, out var info), "GetOutputStreamInfo");
            _provides = (info.dwFlags & 0x300) != 0;
            int need = Math.Max((int)info.cbSize, _stride * _outH * 3 / 2);
            if (!_provides && (need > _outSize || _outSample == IntPtr.Zero))
            {
                Com.Release(ref _outSample); Com.Release(ref _outBuffer);
                _outSize = need;
                _outSample = MF.CreateOutputSample(_outSize, out _outBuffer);
            }
        }

        /// <summary>Decodes one access unit; calls onFrame for every decoded picture. Returns the number of pictures.</summary>
        public int Decode(byte[] data, int offset, int count, Nv12FrameHandler onFrame)
        {
            IntPtr sample;
            fixed (byte* p = data) sample = MF.CreateSample(p + offset, count, _t, 0);
            _t += 166_666;
            int frames = 0, hr;
            try
            {
                hr = MF.ProcessInput(_mft, sample);
                if (hr == MF.E_NOTACCEPTING) { frames += Drain(onFrame); hr = MF.ProcessInput(_mft, sample); }
            }
            finally { Com.Release(sample); }
            Com.Check(hr, "ProcessInput(decoder)");
            return frames + Drain(onFrame);
        }

        int Drain(Nv12FrameHandler onFrame)
        {
            int frames = 0;
            for (int guard = 0; guard < 16; guard++)
            {
                var db = new MFT_OUTPUT_DATA_BUFFER { pSample = _provides ? IntPtr.Zero : _outSample };
                if (!_provides) MF.SetCurrentLength(_outBuffer, 0);
                int hr = MF.ProcessOutput(_mft, &db, out _);
                if (db.pEvents != IntPtr.Zero) Com.Release(db.pEvents);
                if (hr == MF.E_NEED_MORE_INPUT) break;
                if (hr == MF.E_STREAM_CHANGE) { SetupOutput(); continue; }
                Com.Check(hr, "ProcessOutput(decoder)");
                try
                {
                    if (MF.ConvertToContiguousBuffer(db.pSample, out IntPtr buf) >= 0)
                    {
                        if (MF.Lock(buf, out byte* p, out uint len) >= 0)
                        {
                            int stride = _stride, planeH = _outH;
                            if ((long)stride * planeH * 3 / 2 > len && stride > 0) planeH = (int)(len * 2 / 3 / stride);
                            if (planeH > 0) onFrame(p, p + (long)stride * planeH, stride, _outW, Math.Min(_outH, planeH));
                            MF.Unlock(buf);
                            frames++;
                        }
                        Com.Release(buf);
                    }
                }
                finally { if (_provides) Com.Release(db.pSample); }
            }
            return frames;
        }

        public void Dispose()
        {
            Com.Release(ref _outSample);
            Com.Release(ref _outBuffer);
            Com.Release(ref _mft);
        }
    }
}
