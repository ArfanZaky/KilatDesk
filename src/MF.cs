using System;
using System.Runtime.InteropServices;

namespace KilatDesk
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags, cbSize, cbAlignment; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public IntPtr pSample; public uint dwStatus; public IntPtr pEvents; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct VARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public short boolVal;
    }

    /// <summary>Media Foundation constants + thin vtable wrappers (IMFAttributes, IMFTransform, IMFSample, IMFMediaBuffer, ICodecAPI).</summary>
    internal static unsafe class MF
    {
        public static readonly Guid MediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid Format_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid Format_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");

        public static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public static readonly Guid TRANSFORM_ASYNC = new Guid("f81a699a-649a-497d-8c73-29f8fed6ad7a");
        public static readonly Guid TRANSFORM_ASYNC_UNLOCK = new Guid("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static readonly Guid FRIENDLY_NAME = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static readonly Guid SampleCleanPoint = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

        public static readonly Guid CAT_VIDEO_ENCODER = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        public static readonly Guid CLSID_H264Encoder = new Guid("6ca50344-051a-4ded-9779-a43305165e35");
        public static readonly Guid CLSID_H264Decoder = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        public static readonly Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        public static readonly Guid IID_ICodecAPI = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        public static readonly Guid IID_EventGenerator = new Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
        public static readonly Guid IID_IMFShutdown = new Guid("97ec2ea4-0e42-4937-97ac-9d6d328824e1");

        public static readonly Guid API_RateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static readonly Guid API_MeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        public static readonly Guid API_QualityVsSpeed = new Guid("98332df8-03cd-476b-89fa-3f9e442dec9f");
        public static readonly Guid API_GOPSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        public static readonly Guid API_BPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        public static readonly Guid API_ForceKeyFrame = new Guid("398c1b98-8353-475a-9ef2-8f265d260345");
        public static readonly Guid API_LowLatency = LOW_LATENCY;

        public const int E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int E_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        public const uint MSG_FLUSH = 0, MSG_BEGIN_STREAMING = 0x10000000, MSG_END_STREAMING = 0x10000001, MSG_START_OF_STREAM = 0x10000003;

        static readonly object StartGate = new object();
        static bool _started;

        public static void Startup()
        {
            lock (StartGate)
            {
                if (_started) return;
                Native.EnsureMta();
                Com.Check(Native.MFStartup(0x00020070, 0), "MFStartup");
                _started = true;
            }
        }

        public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

        // ---------------- IMFAttributes (also the first 30 methods of IMFMediaType / IMFSample / IMFActivate / IMFMediaEvent)
        public static int GetU32(IntPtr a, Guid key, out uint v) { uint r = 0; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Com.Vt(a, 7))(a, &key, &r); v = r; return hr; }
        public static int GetU64(IntPtr a, Guid key, out ulong v) { ulong r = 0; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Com.Vt(a, 8))(a, &key, &r); v = r; return hr; }
        public static int GetGuid(IntPtr a, Guid key, out Guid v) { Guid r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Com.Vt(a, 10))(a, &key, &r); v = r; return hr; }
        public static int SetU32(IntPtr a, Guid key, uint v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Com.Vt(a, 21))(a, &key, v);
        public static int SetU64(IntPtr a, Guid key, ulong v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Com.Vt(a, 22))(a, &key, v);
        public static int SetGuid(IntPtr a, Guid key, Guid v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Com.Vt(a, 24))(a, &key, &v);

        public static string GetString(IntPtr a, Guid key)
        {
            IntPtr s = IntPtr.Zero; uint len;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)Com.Vt(a, 13))(a, &key, &s, &len) < 0 || s == IntPtr.Zero) return null;
            string r = Marshal.PtrToStringUni(s);
            Native.CoTaskMemFree(s);
            return r;
        }

        public static IntPtr CreateVideoType(Guid subtype, int w, int h, int fps)
        {
            Com.Check(Native.MFCreateMediaType(out IntPtr mt), "MFCreateMediaType");
            SetGuid(mt, MT_MAJOR_TYPE, MediaType_Video);
            SetGuid(mt, MT_SUBTYPE, subtype);
            if (w > 0) SetU64(mt, MT_FRAME_SIZE, Pack(w, h));
            if (fps > 0) SetU64(mt, MT_FRAME_RATE, Pack(fps, 1));
            SetU32(mt, MT_INTERLACE_MODE, 2); // progressive
            SetU64(mt, MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            return mt;
        }

        // ---------------- IMFTransform
        public static int GetOutputStreamInfo(IntPtr t, out MFT_OUTPUT_STREAM_INFO info) { MFT_OUTPUT_STREAM_INFO i; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, MFT_OUTPUT_STREAM_INFO*, int>)Com.Vt(t, 7))(t, 0, &i); info = i; return hr; }
        public static int GetAttributes(IntPtr t, out IntPtr attrs) { IntPtr a = IntPtr.Zero; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Vt(t, 8))(t, &a); attrs = a; return hr; }
        public static int GetInputAvailableType(IntPtr t, uint i, out IntPtr mt) { IntPtr m = IntPtr.Zero; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Com.Vt(t, 13))(t, 0, i, &m); mt = m; return hr; }
        public static int GetOutputAvailableType(IntPtr t, uint i, out IntPtr mt) { IntPtr m = IntPtr.Zero; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Com.Vt(t, 14))(t, 0, i, &m); mt = m; return hr; }
        public static int SetInputType(IntPtr t, IntPtr mt) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Vt(t, 15))(t, 0, mt, 0);
        public static int SetOutputType(IntPtr t, IntPtr mt) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Vt(t, 16))(t, 0, mt, 0);
        public static int GetOutputCurrentType(IntPtr t, out IntPtr mt) { IntPtr m = IntPtr.Zero; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Vt(t, 18))(t, 0, &m); mt = m; return hr; }
        public static int ProcessMessage(IntPtr t, uint msg) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, int>)Com.Vt(t, 23))(t, msg, UIntPtr.Zero);
        public static int ProcessInput(IntPtr t, IntPtr sample) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Vt(t, 24))(t, 0, sample, 0);
        public static int ProcessOutput(IntPtr t, MFT_OUTPUT_DATA_BUFFER* buf, out uint status) { uint s = 0; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)Com.Vt(t, 25))(t, 0, 1, buf, &s); status = s; return hr; }

        // ---------------- IMFSample / IMFMediaBuffer
        public static int SetSampleTime(IntPtr s, long t) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Vt(s, 36))(s, t);
        public static int SetSampleDuration(IntPtr s, long d) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Vt(s, 38))(s, d);
        public static int ConvertToContiguousBuffer(IntPtr s, out IntPtr b) { IntPtr r = IntPtr.Zero; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Vt(s, 41))(s, &r); b = r; return hr; }
        public static int AddBuffer(IntPtr s, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Vt(s, 42))(s, b);
        public static int Lock(IntPtr b, out byte* p, out uint cur) { byte* pp = null; uint max = 0, c = 0; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Vt(b, 3))(b, &pp, &max, &c); p = pp; cur = c; return hr; }
        public static int Unlock(IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(b, 4))(b);
        public static int SetCurrentLength(IntPtr b, uint len) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Vt(b, 6))(b, len);

        /// <summary>Creates an IMFSample holding one memory buffer with a copy of the given bytes.</summary>
        public static IntPtr CreateSample(byte* data, int len, long time, long duration)
        {
            Com.Check(Native.MFCreateMemoryBuffer((uint)Math.Max(len, 1), out IntPtr buf), "MFCreateMemoryBuffer");
            try
            {
                Com.Check(Lock(buf, out byte* p, out _), "Lock");
                Buffer.MemoryCopy(data, p, len, len);
                Unlock(buf);
                SetCurrentLength(buf, (uint)len);
                Com.Check(Native.MFCreateSample(out IntPtr s), "MFCreateSample");
                AddBuffer(s, buf);
                SetSampleTime(s, time);
                if (duration > 0) SetSampleDuration(s, duration);
                return s;
            }
            finally { Com.Release(buf); }
        }

        /// <summary>Creates an empty sample with an output buffer of the given capacity.</summary>
        public static IntPtr CreateOutputSample(int capacity, out IntPtr buffer)
        {
            Com.Check(Native.MFCreateMemoryBuffer((uint)capacity, out buffer), "MFCreateMemoryBuffer");
            Com.Check(Native.MFCreateSample(out IntPtr s), "MFCreateSample");
            AddBuffer(s, buffer);
            return s;
        }

        // ---------------- ICodecAPI::SetValue (slot 9)
        public static int CodecSetU32(IntPtr api, Guid key, uint v)
        {
            if (api == IntPtr.Zero) return -1;
            VARIANT var = default; var.vt = 19; var.ulVal = v;
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, VARIANT*, int>)Com.Vt(api, 9))(api, &key, &var);
        }

        public static int CodecSetBool(IntPtr api, Guid key, bool v)
        {
            if (api == IntPtr.Zero) return -1;
            VARIANT var = default; var.vt = 11; var.boolVal = (short)(v ? -1 : 0);
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, VARIANT*, int>)Com.Vt(api, 9))(api, &key, &var);
        }
    }
}
