using System;
using System.Runtime.InteropServices;

namespace FlashbackStudio
{
    // Stereo 16-bit sound out through Windows' waveOut: Buffers buffers of
    // BufferSamples samples each, queued one after another (like the 3DS app's
    // NDSP wave buffers).
    sealed class AudioOut : IDisposable
    {
        public const int Buffers = 4;
        public const int BufferSamples = 4096;      // 125 ms each: half a second queued

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat
        {
            public short FormatTag, Channels;
            public int SamplesPerSec, AvgBytesPerSec;
            public short BlockAlign, BitsPerSample, Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader
        {
            public IntPtr Data;
            public int BufferLength, BytesRecorded;
            public IntPtr User;
            public int Flags, Loops;
            public IntPtr Next, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MmTime
        {
            public int Type;
            public int Value;
            public int Pad;
        }

        const int WaveMapper = -1, CallbackNull = 0, HeaderDone = 1, TimeSamples = 2;

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr wave, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr wave, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr wave, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr wave, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr wave);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr wave);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr wave, ref MmTime time, int size);

        readonly IntPtr wave;
        readonly IntPtr[] headers = new IntPtr[Buffers], data = new IntPtr[Buffers];
        readonly bool[] prepared = new bool[Buffers];
        static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));
        static readonly int FlagsAt = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");

        // Opens the sound output at 'rate' samples a second. Throws if there's none.
        public AudioOut(int rate)
        {
            WaveFormat format = new WaveFormat
            {
                FormatTag = 1,      // PCM
                Channels = 2,
                SamplesPerSec = rate,
                AvgBytesPerSec = rate * 4,
                BlockAlign = 4,
                BitsPerSample = 16,
            };
            int result = waveOutOpen(out wave, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
            if (result != 0)
                throw new InvalidOperationException($"the sound output couldn't be opened (error {result})");
            for (int i = 0; i < Buffers; i++)
            {
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                data[i] = Marshal.AllocHGlobal(BufferSamples * 4);
                Marshal.StructureToPtr(new WaveHeader { Flags = HeaderDone }, headers[i], false);
            }
        }

        // Buffer 'i' has played (or was never queued), so it can be filled again.
        public bool IsDone(int i) => (Marshal.ReadInt32(headers[i], FlagsAt) & HeaderDone) != 0;

        // Queues 'count' samples of 'samples' (left, right, ...) in buffer 'i'.
        public void Queue(int i, short[] samples, int count)
        {
            if (prepared[i])
                waveOutUnprepareHeader(wave, headers[i], HeaderSize);
            Marshal.Copy(samples, 0, data[i], count * 2);
            Marshal.StructureToPtr(new WaveHeader { Data = data[i], BufferLength = count * 4 }, headers[i], false);
            prepared[i] = waveOutPrepareHeader(wave, headers[i], HeaderSize) == 0;
            if (!prepared[i] || waveOutWrite(wave, headers[i], HeaderSize) != 0)
                Marshal.StructureToPtr(new WaveHeader { Flags = HeaderDone }, headers[i], false);
        }

        // Samples played since the last Stop (or since it was opened).
        public long Played
        {
            get
            {
                MmTime time = new MmTime { Type = TimeSamples };
                waveOutGetPosition(wave, ref time, Marshal.SizeOf(typeof(MmTime)));
                return time.Type == TimeSamples ? (uint)time.Value : 0;
            }
        }

        // Stops the sound and lets go of every buffer (they're all done after).
        public void Stop()
        {
            waveOutReset(wave);
        }

        public void Dispose()
        {
            waveOutReset(wave);
            for (int i = 0; i < Buffers; i++)
            {
                if (prepared[i])
                    waveOutUnprepareHeader(wave, headers[i], HeaderSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(data[i]);
            }
            waveOutClose(wave);
        }
    }
}
