using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KilatDesk
{
    /// <summary>IMA ADPCM: 4 bits per sample (16-bit PCM -> 1/4 size). Each packet carries its own start state.</summary>
    internal static class Adpcm
    {
        static readonly int[] Steps = {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
            22385, 24623, 27086, 29794, 32767 };
        static readonly int[] IndexAdj = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        public struct State { public int Pred, Index; }

        static int Step(ref State s, int code)
        {
            int step = Steps[s.Index], delta = step >> 3;
            if ((code & 4) != 0) delta += step;
            if ((code & 2) != 0) delta += step >> 1;
            if ((code & 1) != 0) delta += step >> 2;
            s.Pred += (code & 8) != 0 ? -delta : delta;
            if (s.Pred > 32767) s.Pred = 32767; else if (s.Pred < -32768) s.Pred = -32768;
            s.Index += IndexAdj[code];
            if (s.Index < 0) s.Index = 0; else if (s.Index > 88) s.Index = 88;
            return s.Pred;
        }

        static int EncodeSample(ref State s, int sample)
        {
            int diff = sample - s.Pred, step = Steps[s.Index], code = 0;
            if (diff < 0) { code = 8; diff = -diff; }
            if (diff >= step) { code |= 4; diff -= step; }
            if (diff >= step >> 1) { code |= 2; diff -= step >> 1; }
            if (diff >= step >> 2) code |= 1;
            Step(ref s, code);
            return code;
        }

        /// <summary>Packet: [channels][frames:u16] + per channel [pred:i16][index:u8] + nibbles (frame-major).</summary>
        public static byte[] Encode(short[] pcm, int frames, int channels, State[] st)
        {
            int nibbles = frames * channels;
            var o = new byte[3 + 3 * channels + (nibbles + 1) / 2];
            o[0] = (byte)channels; o[1] = (byte)frames; o[2] = (byte)(frames >> 8);
            for (int c = 0; c < channels; c++)
            {
                o[3 + c * 3] = (byte)st[c].Pred; o[4 + c * 3] = (byte)(st[c].Pred >> 8); o[5 + c * 3] = (byte)st[c].Index;
            }
            int p = 3 + 3 * channels;
            for (int i = 0; i < nibbles; i++)
            {
                int code = EncodeSample(ref st[i % channels], pcm[i]);
                if ((i & 1) == 0) o[p + (i >> 1)] = (byte)code; else o[p + (i >> 1)] |= (byte)(code << 4);
            }
            return o;
        }

        public static short[] Decode(byte[] d, int offset, out int channels, out int frames)
        {
            channels = d[offset]; frames = d[offset + 1] | (d[offset + 2] << 8);
            if (channels < 1 || channels > 2) { frames = 0; return new short[0]; }
            var st = new State[channels];
            for (int c = 0; c < channels; c++)
            {
                st[c].Pred = (short)(d[offset + 3 + c * 3] | (d[offset + 4 + c * 3] << 8));
                st[c].Index = Math.Min(88, (int)d[offset + 5 + c * 3]);
            }
            int p = offset + 3 + 3 * channels, n = frames * channels;
            if (p + (n + 1) / 2 > d.Length) { frames = 0; return new short[0]; }
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
            {
                int code = (i & 1) == 0 ? d[p + (i >> 1)] & 15 : d[p + (i >> 1)] >> 4;
                pcm[i] = (short)Step(ref st[i % channels], code);
            }
            return pcm;
        }
    }

    /// <summary>Records what the host PC is playing (WASAPI loopback of the default speakers), as 16-bit stereo.</summary>
    internal sealed unsafe class AudioCapture : IDisposable
    {
        static readonly Guid CLSID_Enumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        static readonly Guid IID_Enumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        static readonly Guid IID_AudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid IID_CaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public event Action<int, int> FormatReady;       // sample rate, channels (always 2)
        public event Action<short[], int> Samples;       // interleaved stereo, frame count
        public string Error { get; private set; }
        Thread _t;
        volatile bool _stop;

        public void Start()
        {
            _t = new Thread(Run) { IsBackground = true, Name = "audio capture" };
            _t.Start();
        }

        void Run()
        {
            Native.EnsureMta();
            while (!_stop)
            {
                try { CaptureLoop(); }
                catch (Exception ex) { Error = ex.Message; Log.Warn("audio capture: " + ex.Message); }
                for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100); // retry (e.g. default device changed)
            }
        }

        void CaptureLoop()
        {
            IntPtr en = IntPtr.Zero, dev = IntPtr.Zero, client = IntPtr.Zero, cap = IntPtr.Zero, fmt = IntPtr.Zero;
            try
            {
                Guid clsid = CLSID_Enumerator, iid = IID_Enumerator;
                Com.Check(Native.CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out en), "MMDeviceEnumerator");
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Com.Vt(en, 4))(en, 0, 0, &dev), "GetDefaultAudioEndpoint");
                Guid iac = IID_AudioClient;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Com.Vt(dev, 3))(dev, &iac, 0x17, IntPtr.Zero, &client), "Activate IAudioClient");
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Vt(client, 8))(client, &fmt), "GetMixFormat");
                ushort tag = *(ushort*)fmt, ch = *(ushort*)((byte*)fmt + 2), bits = *(ushort*)((byte*)fmt + 14);
                int rate = *(int*)((byte*)fmt + 4);
                bool isFloat = tag == 3 || (tag == 0xFFFE && *(Guid*)((byte*)fmt + 24) == SubtypeFloat);
                if (!(isFloat && bits == 32) && bits != 16 && bits != 32) throw new NotSupportedException("format audio " + bits + " bit");
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, Guid*, int>)Com.Vt(client, 3))(client, 0, 0x00020000, 2_000_000, 0, fmt, null), "IAudioClient.Initialize(loopback)");
                Guid icc = IID_CaptureClient;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Vt(client, 14))(client, &icc, &cap), "GetService(IAudioCaptureClient)");
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(client, 10))(client), "IAudioClient.Start");
                Error = null;
                FormatReady?.Invoke(rate, 2);
                while (!_stop)
                {
                    Thread.Sleep(10);
                    uint packet;
                    Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Vt(cap, 5))(cap, &packet), "GetNextPacketSize");
                    while (packet > 0 && !_stop)
                    {
                        byte* data; uint frames, flags; ulong pos, qpc;
                        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Vt(cap, 3))(cap, &data, &frames, &flags, &pos, &qpc), "GetBuffer");
                        var pcm = new short[frames * 2];
                        if ((flags & 2) == 0) // not AUDCLNT_BUFFERFLAGS_SILENT
                        {
                            for (int i = 0; i < frames; i++)
                            {
                                for (int c = 0; c < 2; c++)
                                {
                                    int src = ch == 1 ? 0 : c;
                                    int s;
                                    if (isFloat) { float f = ((float*)data)[i * ch + src]; s = (int)(f * 32767f); }
                                    else if (bits == 16) s = ((short*)data)[i * ch + src];
                                    else s = ((int*)data)[i * ch + src] >> 16;
                                    pcm[i * 2 + c] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
                                }
                            }
                        }
                        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Vt(cap, 4))(cap, frames);
                        Samples?.Invoke(pcm, (int)frames);
                        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Vt(cap, 5))(cap, &packet), "GetNextPacketSize");
                    }
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(client, 11))(client);
            }
            finally
            {
                if (fmt != IntPtr.Zero) Native.CoTaskMemFree(fmt);
                Com.Release(cap); Com.Release(client); Com.Release(dev); Com.Release(en);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _t?.Join(1500);
        }
    }

    /// <summary>Host side: capture -> 20 ms ADPCM packets -> connection.</summary>
    internal sealed class AudioStreamer : IDisposable
    {
        readonly Connection _conn;
        readonly AudioCapture _cap = new AudioCapture();
        readonly Adpcm.State[] _st = new Adpcm.State[2];
        short[] _acc = new short[0];
        int _accFrames, _packetFrames = 960;

        public AudioStreamer(Connection conn)
        {
            _conn = conn;
            _cap.FormatReady += (rate, ch) =>
            {
                _packetFrames = rate / 50;
                _acc = new short[_packetFrames * 2]; _accFrames = 0;
                _conn.Send(Msg.AudioFormat, w => { w.Write(rate); w.Write((byte)ch); });
            };
            _cap.Samples += OnSamples;
            _cap.Start();
        }

        void OnSamples(short[] pcm, int frames)
        {
            int i = 0;
            while (i < frames)
            {
                int n = Math.Min(frames - i, _packetFrames - _accFrames);
                Array.Copy(pcm, i * 2, _acc, _accFrames * 2, n * 2);
                _accFrames += n; i += n;
                if (_accFrames == _packetFrames)
                {
                    if (_conn.QueuedBytes < 2 * 1024 * 1024) _conn.Send(Msg.AudioData, Adpcm.Encode(_acc, _accFrames, 2, _st));
                    _accFrames = 0;
                }
            }
        }

        public void Dispose() => _cap.Dispose();
    }

    /// <summary>Viewer side: plays 16-bit PCM with waveOut and a small jitter buffer (~60-150 ms).</summary>
    internal sealed unsafe class AudioPlayer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, uint cb);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, uint cb);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, uint cb);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        const int NBuf = 4;
        readonly int _channels, _bufSamples;
        readonly int _rate;
        IntPtr _hwo;
        readonly IntPtr[] _hdr = new IntPtr[NBuf], _data = new IntPtr[NBuf];
        readonly short[] _ring;
        int _rHead, _rCount;
        readonly object _gate = new object();
        Thread _t;
        volatile bool _stop;

        public AudioPlayer(int rate, int channels)
        {
            _rate = rate; _channels = channels;
            _bufSamples = rate / 50 * channels; // 20 ms per buffer
            _ring = new short[rate * channels];  // 1 s
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = (ushort)(channels * 2), nAvgBytesPerSec = (uint)(rate * channels * 2) };
            int r = waveOutOpen(out _hwo, 0xFFFFFFFF, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new InvalidOperationException("waveOutOpen gagal (" + r + ")");
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < NBuf; i++)
            {
                _data[i] = Marshal.AllocHGlobal(_bufSamples * 2);
                _hdr[i] = Marshal.AllocHGlobal(hs);
                var h = new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)(_bufSamples * 2), dwFlags = 1 /*DONE: free to fill*/ };
                Marshal.StructureToPtr(h, _hdr[i], false);
            }
            _t = new Thread(Loop) { IsBackground = true, Name = "audio play", Priority = ThreadPriority.AboveNormal };
            _t.Start();
        }

        public void Feed(short[] pcm)
        {
            lock (_gate)
            {
                int target = _rate * _channels * 150 / 1000; // keep latency below ~150 ms
                foreach (short s in pcm)
                {
                    if (_rCount == _ring.Length) { _rHead = (_rHead + 1) % _ring.Length; _rCount--; }
                    _ring[(_rHead + _rCount) % _ring.Length] = s; _rCount++;
                }
                if (_rCount > target * 2) { int drop = _rCount - target; drop -= drop % _channels; _rHead = (_rHead + drop) % _ring.Length; _rCount -= drop; }
            }
        }

        void Loop()
        {
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            while (!_stop)
            {
                bool wrote = false;
                for (int i = 0; i < NBuf; i++)
                {
                    var h = (WAVEHDR*)_hdr[i];
                    if ((h->dwFlags & 1) == 0) continue; // still playing
                    if ((h->dwFlags & 2) != 0) waveOutUnprepareHeader(_hwo, _hdr[i], (uint)hs);
                    short* dst = (short*)_data[i];
                    lock (_gate)
                    {
                        int n = Math.Min(_rCount, _bufSamples);
                        for (int k = 0; k < n; k++) dst[k] = _ring[(_rHead + k) % _ring.Length];
                        for (int k = n; k < _bufSamples; k++) dst[k] = 0;
                        _rHead = (_rHead + n) % _ring.Length; _rCount -= n;
                    }
                    h->dwFlags = 0; h->dwBufferLength = (uint)(_bufSamples * 2);
                    waveOutPrepareHeader(_hwo, _hdr[i], (uint)hs);
                    waveOutWrite(_hwo, _hdr[i], (uint)hs);
                    wrote = true;
                }
                if (!wrote) Thread.Sleep(4);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _t?.Join(500);
            if (_hwo != IntPtr.Zero)
            {
                waveOutReset(_hwo);
                int hs = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < NBuf; i++) if (_hdr[i] != IntPtr.Zero) waveOutUnprepareHeader(_hwo, _hdr[i], (uint)hs);
                waveOutClose(_hwo);
                _hwo = IntPtr.Zero;
            }
            for (int i = 0; i < NBuf; i++)
            {
                if (_hdr[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_hdr[i]); _hdr[i] = IntPtr.Zero; }
                if (_data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_data[i]); _data[i] = IntPtr.Zero; }
            }
        }
    }
}
