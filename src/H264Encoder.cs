using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KilatDesk
{
    /// <summary>
    /// H.264 encoder on top of Media Foundation. Tries GPU encoders first (NVENC / QuickSync / AMF, which are
    /// asynchronous MFTs) and falls back to the Microsoft software encoder. Packets are delivered via a callback.
    /// </summary>
    internal sealed unsafe class H264Encoder : IDisposable
    {
        readonly int _w, _h, _fps;
        int _kbps;
        readonly Action<byte[], bool> _onPacket;
        IntPtr _mft, _codecApi, _activate, _events, _outSample, _outBuffer;
        bool _async, _provides;
        int _outSize;
        Thread _eventThread;
        volatile bool _stop;
        int _needInput;
        readonly AutoResetEvent _needInputSignal = new AutoResetEvent(false);
        readonly object _outGate = new object();
        public string Name { get; private set; }
        public bool IsHardware => _async;

        H264Encoder(int w, int h, int fps, int kbps, Action<byte[], bool> onPacket)
        {
            _w = w; _h = h; _fps = fps; _kbps = kbps; _onPacket = onPacket;
        }

        public static H264Encoder Create(int w, int h, int fps, int kbps, bool allowHardware, Action<byte[], bool> onPacket)
        {
            MF.Startup();
            if (allowHardware)
            {
                var acts = EnumHardware();
                for (int i = 0; i < acts.Count; i++)
                {
                    var e = new H264Encoder(w, h, fps, kbps, onPacket);
                    try
                    {
                        e.InitHardware(acts[i]);
                        for (int j = i + 1; j < acts.Count; j++) Com.Release(acts[j]);
                        return e;
                    }
                    catch (Exception ex) { Log.Warn("Encoder GPU gagal (" + e.Name + "): " + ex.Message); e.Dispose(); }
                }
            }
            var sw = new H264Encoder(w, h, fps, kbps, onPacket);
            try { sw.InitSoftware(); return sw; }
            catch { sw.Dispose(); throw; }
        }

        static List<IntPtr> EnumHardware()
        {
            var list = new List<IntPtr>();
            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = MF.MediaType_Video, guidSubtype = MF.Format_NV12 };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = MF.MediaType_Video, guidSubtype = MF.Format_H264 };
            if (Native.MFTEnumEx(MF.CAT_VIDEO_ENCODER, 0x4 | 0x40, ref input, ref output, out IntPtr arr, out uint n) < 0 || arr == IntPtr.Zero)
                return list;
            for (int i = 0; i < n; i++) list.Add(Marshal.ReadIntPtr(arr, i * IntPtr.Size));
            Native.CoTaskMemFree(arr);
            return list;
        }

        void InitHardware(IntPtr activate)
        {
            _activate = activate;
            Name = MF.GetString(activate, MF.FRIENDLY_NAME) ?? "GPU H.264";
            Guid iid = MF.IID_IMFTransform;
            IntPtr mft;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Vt(activate, 33))(activate, &iid, &mft), "ActivateObject");
            _mft = mft;
            if (MF.GetAttributes(_mft, out IntPtr attrs) >= 0 && attrs != IntPtr.Zero)
            {
                if (MF.GetU32(attrs, MF.TRANSFORM_ASYNC, out uint isAsync) >= 0 && isAsync != 0)
                {
                    Com.Check(MF.SetU32(attrs, MF.TRANSFORM_ASYNC_UNLOCK, 1), "ASYNC_UNLOCK");
                    _async = true;
                }
                MF.SetU32(attrs, MF.LOW_LATENCY, 1);
                Com.Release(attrs);
            }
            Configure(false);
            if (_async)
            {
                Com.Check(Com.QI(_mft, MF.IID_EventGenerator, out _events), "QI IMFMediaEventGenerator");
                _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "H264 events" };
                _eventThread.Start();
            }
            Start();
        }

        void InitSoftware()
        {
            Name = "Microsoft H.264 (CPU)";
            Guid clsid = MF.CLSID_H264Encoder, iid = MF.IID_IMFTransform;
            Com.Check(Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out _mft), "CoCreateInstance(H264 encoder)");
            if (MF.GetAttributes(_mft, out IntPtr attrs) >= 0 && attrs != IntPtr.Zero) { MF.SetU32(attrs, MF.LOW_LATENCY, 1); Com.Release(attrs); }
            Configure(true);
            Start();
        }

        void Configure(bool software)
        {
            Com.QI(_mft, MF.IID_ICodecAPI, out _codecApi);
            MF.CodecSetBool(_codecApi, MF.API_LowLatency, true);
            MF.CodecSetU32(_codecApi, MF.API_RateControlMode, 0); // CBR: stable frame sizes = stable latency
            MF.CodecSetU32(_codecApi, MF.API_MeanBitRate, (uint)_kbps * 1000);
            MF.CodecSetU32(_codecApi, MF.API_BPictureCount, 0);
            MF.CodecSetU32(_codecApi, MF.API_GOPSize, (uint)(_fps * 60)); // keyframes mostly on request
            MF.CodecSetU32(_codecApi, MF.API_QualityVsSpeed, software ? 15u : 50u);

            int hr = -1;
            foreach (uint profile in new uint[] { 100, 77, 66 }) // High, Main, Baseline
            {
                IntPtr ot = MF.CreateVideoType(MF.Format_H264, _w, _h, _fps);
                MF.SetU32(ot, MF.MT_AVG_BITRATE, (uint)_kbps * 1000);
                MF.SetU32(ot, MF.MT_MPEG2_PROFILE, profile);
                hr = MF.SetOutputType(_mft, ot);
                Com.Release(ot);
                if (hr >= 0) break;
            }
            Com.Check(hr, "SetOutputType(H264)");

            IntPtr it = MF.CreateVideoType(MF.Format_NV12, _w, _h, _fps);
            hr = MF.SetInputType(_mft, it);
            Com.Release(it);
            if (hr < 0)
            {
                // Some encoders want us to start from one of their own proposed input types.
                for (uint i = 0; hr < 0 && MF.GetInputAvailableType(_mft, i, out IntPtr at) >= 0; i++)
                {
                    if (MF.GetGuid(at, MF.MT_SUBTYPE, out Guid st) >= 0 && st == MF.Format_NV12)
                    {
                        MF.SetU64(at, MF.MT_FRAME_SIZE, MF.Pack(_w, _h));
                        MF.SetU64(at, MF.MT_FRAME_RATE, MF.Pack(_fps, 1));
                        hr = MF.SetInputType(_mft, at);
                    }
                    Com.Release(at);
                }
            }
            Com.Check(hr, "SetInputType(NV12)");
            MF.CodecSetU32(_codecApi, MF.API_MeanBitRate, (uint)_kbps * 1000);

            Com.Check(MF.GetOutputStreamInfo(_mft, out var info), "GetOutputStreamInfo");
            _provides = (info.dwFlags & 0x300) != 0;
            _outSize = Math.Max((int)info.cbSize, _w * _h * 2);
            if (!_provides) _outSample = MF.CreateOutputSample(_outSize, out _outBuffer);
        }

        void Start()
        {
            MF.ProcessMessage(_mft, MF.MSG_BEGIN_STREAMING);
            MF.ProcessMessage(_mft, MF.MSG_START_OF_STREAM);
        }

        public void SetBitrate(int kbps)
        {
            _kbps = kbps;
            MF.CodecSetU32(_codecApi, MF.API_MeanBitRate, (uint)kbps * 1000);
        }

        /// <summary>Feeds one NV12 frame. Returns false if the (GPU) encoder was still busy and the frame was skipped.</summary>
        public bool Encode(byte* nv12, long time100ns, bool forceKey)
        {
            if (_async)
            {
                if (Volatile.Read(ref _needInput) <= 0) _needInputSignal.WaitOne(100);
                if (Interlocked.Decrement(ref _needInput) < 0) { Interlocked.Increment(ref _needInput); return false; }
            }
            if (forceKey) MF.CodecSetU32(_codecApi, MF.API_ForceKeyFrame, 1);
            IntPtr sample = MF.CreateSample(nv12, _w * _h * 3 / 2, time100ns, 10_000_000L / _fps);
            int hr;
            try
            {
                hr = MF.ProcessInput(_mft, sample);
                if (hr == MF.E_NOTACCEPTING && !_async) { DrainOutputs(); hr = MF.ProcessInput(_mft, sample); }
            }
            finally { Com.Release(sample); }
            Com.Check(hr, "ProcessInput(encoder)");
            if (!_async) DrainOutputs();
            return true;
        }

        void DrainOutputs()
        {
            while (!_stop && PullOne()) { }
        }

        /// <summary>Pulls one output sample. Returns false when the encoder needs more input.</summary>
        bool PullOne()
        {
            lock (_outGate)
            {
                var db = new MFT_OUTPUT_DATA_BUFFER { pSample = _provides ? IntPtr.Zero : _outSample };
                if (!_provides) MF.SetCurrentLength(_outBuffer, 0);
                int hr = MF.ProcessOutput(_mft, &db, out _);
                if (db.pEvents != IntPtr.Zero) Com.Release(db.pEvents);
                if (hr == MF.E_NEED_MORE_INPUT) return false;
                if (hr == MF.E_STREAM_CHANGE)
                {
                    if (MF.GetOutputAvailableType(_mft, 0, out IntPtr t) >= 0) { MF.SetOutputType(_mft, t); Com.Release(t); }
                    return true;
                }
                if (hr < 0) { Log.Warn("ProcessOutput(encoder) 0x" + hr.ToString("X8")); return false; }
                try { Emit(db.pSample); }
                finally { if (_provides) Com.Release(db.pSample); }
                return true;
            }
        }

        void Emit(IntPtr sample)
        {
            if (sample == IntPtr.Zero) return;
            if (MF.ConvertToContiguousBuffer(sample, out IntPtr buf) < 0) return;
            try
            {
                if (MF.Lock(buf, out byte* p, out uint len) < 0) return;
                var data = new byte[len];
                fixed (byte* d = data) Buffer.MemoryCopy(p, d, len, len);
                MF.Unlock(buf);
                bool key = MF.GetU32(sample, MF.SampleCleanPoint, out uint cp) >= 0 && cp != 0;
                if (len > 0) _onPacket(data, key);
            }
            finally { Com.Release(buf); }
        }

        void EventLoop()
        {
            Native.EnsureMta();
            while (!_stop)
            {
                IntPtr ev = IntPtr.Zero;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(_events, 3))(_events, 0, &ev);
                if (hr < 0 || ev == IntPtr.Zero) break;
                uint type = 0; int status = 0;
                ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Vt(ev, 33))(ev, &type);
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Com.Vt(ev, 35))(ev, &status);
                Com.Release(ev);
                if (_stop) break;
                if (type == 601) { Interlocked.Increment(ref _needInput); _needInputSignal.Set(); }
                else if (type == 602) { try { PullOne(); } catch (Exception ex) { Log.Error("encoder output", ex); } }
                else if (status < 0) Log.Warn("Encoder event " + type + " status 0x" + status.ToString("X8"));
            }
        }

        public void Dispose()
        {
            _stop = true;
            if (_async && _mft != IntPtr.Zero && Com.QI(_mft, MF.IID_IMFShutdown, out IntPtr sd) >= 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(sd, 3))(sd);
                Com.Release(sd);
            }
            if (_eventThread != null && !_eventThread.Join(2000)) Log.Warn("encoder event thread did not stop");
            _needInputSignal.Set();
            lock (_outGate)
            {
                Com.Release(ref _events);
                Com.Release(ref _codecApi);
                Com.Release(ref _outSample);
                Com.Release(ref _outBuffer);
                Com.Release(ref _mft);
                if (_activate != IntPtr.Zero)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(_activate, 34))(_activate);
                    Com.Release(ref _activate);
                }
            }
        }
    }
}
