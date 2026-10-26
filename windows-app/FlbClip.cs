using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FlashbackStudio
{
    // The inputs during a frame (see 3ds-app/source/flb.h): the buttons held at
    // any time during it (the 3DS's KEY_* bits, with KeyTouch for the touch
    // screen), and where the Circle Pad and the touch screen were.
    struct FlbInput
    {
        public const uint KeyTouch = 1u << 20;

        public uint Buttons;
        public short CircleX, CircleY;      // about -156 to 156, up and right positive
        public ushort TouchX, TouchY;       // (with KeyTouch)
        public short CStickX, CStickY;      // the C-Stick (not recorded yet: 0)
    }

    // Reader for Flashback clip files (.flb), recorded by Luma3DS-Flashback and
    // compacted or trimmed by Flashback Studio. All three versions of the format,
    // and the inputs, are described in 3ds-app/source/flb.h; this follows flb.c,
    // with the same checks and messages.
    //
    // It also reads clips saved by older Flashback builds: raw RGB565 frames back
    // to back in a .raw, with a .txt next to it saying their resolution and frame
    // rate. They play and export like the others (as if each frame were an index
    // entry), but can't be compacted or trimmed.
    sealed class FlbClip : IDisposable
    {
        const uint Magic = 0x4B424C46;          // "FLBK"
        const uint InputMagic = 0x49424C46;     // "FLBI"
        public const int TrailerSize = 48;
        public const int AudioInfoSize = 32;
        public const int InputHeaderSize = 16;
        public const int InputRecordSize = 16;

        // A stored frame: where it is in the file, its size, how many times it's
        // shown, the clip frame it's first shown at, and (with sound) the sample
        // it's first shown at.
        public struct Entry
        {
            public long Offset;
            public int Size, Copies, FirstFrame;
            public long AudioPos;
        }

        public string Path { get; }
        public bool IsRaw { get; private set; }         // an older .raw clip
        public uint Version { get; private set; }       // 1: as recorded, 2: compacted, 3: with sound (either)
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Fps { get; private set; }
        public long SlotStride { get; private set; }    // bytes between ring slots (0 once compacted)
        public int SlotBytes { get; private set; }      // the size of an uncompressed frame
        public int FrameCount { get; private set; }     // frames in the clip, copies included
        public long FileSize { get; private set; }
        public bool HasAudio { get; private set; }
        public long AudioRateMhz { get; private set; }  // the sound's sample rate, in millihertz
        public long AudioMissed { get; private set; }
        public FlbInput[] Inputs { get; private set; }  // the inputs during each frame, or null if the clip has none

        // Compacted already (version 2, or 3 without slots)?
        public bool IsCompacted => Version == 2 || (Version == 3 && SlotStride == 0);
        public int EntryCount => entries.Length;
        public Entry this[int e] => entries[e];

        readonly FileStream file;
        Entry[] entries;            // the stored frames, oldest first
        long audioOffset, audioCapacity, audioFirst, audioEnd;

        FlbClip(string path)
        {
            Path = path;
            try
            {
                // (Others may rename or delete it meanwhile: this program does, while
                // its thumbnails are being read.)
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new InvalidDataException("the file couldn't be opened (" + e.Message.TrimEnd('.') + ")");
            }
        }

        // Opens a clip and reads its index and inputs (the frames are read by
        // DecodeEntry or Frames). Throws InvalidDataException, saying what's
        // wrong, if it can't.
        public static FlbClip Open(string path)
        {
            FlbClip clip = new FlbClip(path);
            try
            {
                if (string.Equals(System.IO.Path.GetExtension(path), ".raw", StringComparison.OrdinalIgnoreCase))
                    clip.OpenRaw();
                else
                    clip.OpenFlb();
                return clip;
            }
            catch
            {
                clip.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            file.Dispose();
        }

        static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

        void ReadAt(long offset, byte[] buf, int at, int len)
        {
            try
            {
                file.Seek(offset, SeekOrigin.Begin);
                for (int got = 0; got < len; )
                {
                    int n = file.Read(buf, at + got, len - got);
                    if (n <= 0)
                        throw new InvalidDataException("the file ends early (was it copied completely?)");
                    got += n;
                }
            }
            catch (IOException e)
            {
                throw new InvalidDataException("the file couldn't be read (" + e.Message.TrimEnd('.') + ")");
            }
        }

        void OpenFlb()
        {
            long length = file.Length;
            if (length < TrailerSize)
                throw new InvalidDataException("not a Flashback clip (file too small)");
            byte[] t = new byte[TrailerSize];
            ReadAt(length - TrailerSize, t, 0, TrailerSize);
            if (U32(t, 0) != Magic)
                throw new InvalidDataException("not a Flashback clip (or the copy is incomplete)");
            uint version = U32(t, 4);
            if (version < 1 || version > 3)
                throw new InvalidDataException("made by a newer Flashback version; get the latest Flashback Studio");
            int entrySize = version == 3 ? 16 : 12;
            int audioInfoSize = version == 3 ? AudioInfoSize : 0;

            uint width = U32(t, 8), height = U32(t, 12), fps = U32(t, 16);
            long stride = U32(t, 20);
            uint slotBytes = U32(t, 24), entryCount = U32(t, 28), frameCount = U32(t, 32);
            long indexOffset = U32(t, 36), inputOffset = U32(t, 40);
            if (width == 0 || height == 0 || width > 400 || height > 240 || slotBytes != width * height * 2 ||
                fps == 0 || fps > 60 || (version == 1 && stride < slotBytes) || entryCount > length / 12 ||
                indexOffset + entryCount * entrySize + audioInfoSize + TrailerSize != length)
                throw new InvalidDataException("the clip's header is damaged");
            Version = version;
            Width = (int)width;
            Height = (int)height;
            Fps = (int)fps;
            SlotStride = stride;
            SlotBytes = (int)slotBytes;
            FrameCount = (int)frameCount;
            FileSize = length;

            byte[] index = new byte[entryCount * entrySize + audioInfoSize];
            ReadAt(indexOffset, index, 0, index.Length);

            if (version == 3)
            {
                int a = (int)entryCount * entrySize;
                HasAudio = true;
                AudioRateMhz = U32(index, a);
                audioOffset = U32(index, a + 8);
                audioCapacity = U32(index, a + 12);
                audioFirst = U32(index, a + 16);
                audioEnd = U32(index, a + 20);
                AudioMissed = U32(index, a + 24);
                if (U16(index, a + 4) != 2 || U16(index, a + 6) != 16 || AudioRateMhz < 8000000 ||
                    AudioRateMhz > 96000000 || audioCapacity == 0 || audioOffset + audioCapacity * 4 > indexOffset ||
                    audioFirst > audioEnd || audioEnd - audioFirst > audioCapacity)
                    throw new InvalidDataException("the clip's audio header is damaged");
            }

            entries = new Entry[entryCount];
            long frames = 0;
            for (int e = 0; e < entryCount; e++)
            {
                long where = U32(index, e * entrySize);     // version 1: ring slot, versions 2 and 3: offset
                long offset = version == 1 ? where * stride : where;
                uint size = U32(index, e * entrySize + 4), copies = U32(index, e * entrySize + 8);
                if (size == 0 || size > slotBytes || copies == 0 || copies > frameCount - frames ||
                    offset + size > indexOffset)
                    throw new InvalidDataException($"frame entry {e} is damaged");
                entries[e] = new Entry
                {
                    Offset = offset,
                    Size = (int)size,
                    Copies = (int)copies,
                    FirstFrame = (int)frames,
                    AudioPos = HasAudio ? U32(index, e * entrySize + 12) : 0,
                };
                frames += copies;
            }
            if (frames != frameCount || entryCount == 0)
                throw new InvalidDataException("the clip's frame count doesn't match its index");

            Inputs = ReadInputs(inputOffset, indexOffset);
        }

        // The clip's inputs, if it has any and they read back sound; a clip whose
        // inputs can't be read plays without them (as in flb.c).
        FlbInput[] ReadInputs(long inputOffset, long indexOffset)
        {
            if (inputOffset == 0 || inputOffset + InputHeaderSize > indexOffset)
                return null;
            byte[] h = new byte[InputHeaderSize];
            ReadAt(inputOffset, h, 0, h.Length);
            uint count = U32(h, 8), size = U32(h, 12);
            if (U32(h, 0) != InputMagic || U32(h, 4) != 1 || count != FrameCount || size < InputRecordSize ||
                size > 256 || inputOffset + InputHeaderSize + (long)count * size > indexOffset)
                return null;

            byte[] records = new byte[count * size];
            ReadAt(inputOffset + InputHeaderSize, records, 0, records.Length);
            FlbInput[] inputs = new FlbInput[count];
            for (int i = 0; i < count; i++)
            {
                int p = i * (int)size;
                inputs[i] = new FlbInput
                {
                    Buttons = U32(records, p),
                    CircleX = (short)U16(records, p + 4),
                    CircleY = (short)U16(records, p + 6),
                    TouchX = U16(records, p + 8),
                    TouchY = U16(records, p + 10),
                    CStickX = (short)U16(records, p + 12),
                    CStickY = (short)U16(records, p + 14),
                };
            }
            return inputs;
        }

        void OpenRaw()
        {
            string txt = System.IO.Path.ChangeExtension(Path, ".txt");
            if (!File.Exists(txt))
                throw new InvalidDataException("its .txt is missing, so its resolution and frame rate are unknown");
            string about;
            try
            {
                about = File.ReadAllText(txt);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new InvalidDataException("its .txt couldn't be read (" + e.Message.TrimEnd('.') + ")");
            }
            Match res = Regex.Match(about, @"Resolution:\s+(\d+)x(\d+)"), fps = Regex.Match(about, @"Frame rate:\s+(\d+)");
            if (!res.Success || !fps.Success ||
                !int.TryParse(res.Groups[1].Value, out int width) || !int.TryParse(res.Groups[2].Value, out int height) ||
                !int.TryParse(fps.Groups[1].Value, out int rate) ||
                width <= 0 || height <= 0 || width > 400 || height > 240 || rate <= 0 || rate > 60)
                throw new InvalidDataException("couldn't read the resolution or frame rate from its .txt");
            IsRaw = true;
            Width = width;
            Height = height;
            Fps = rate;
            SlotBytes = width * height * 2;
            FileSize = file.Length;
            FrameCount = (int)(FileSize / SlotBytes);
            if (FrameCount == 0)
                throw new InvalidDataException("the file ends early (was it copied completely?)");
            entries = new Entry[FrameCount];
            for (int f = 0; f < FrameCount; f++)
                entries[f] = new Entry { Offset = (long)f * SlotBytes, Size = SlotBytes, Copies = 1, FirstFrame = f };
        }

        // The recorder's frame compression, QOI-style ops on RGB565 pixels (see
        // flashbackCompress in Luma3DS-Flashback's flashback_compress.c, and
        // flbDecompress in flb.c). Returns false if the data is malformed.
        static bool Decompress(byte[] src, int len, ushort[] dst, int pixels)
        {
            ushort[] index = new ushort[64];
            ushort px = 0;
            int p = 0, i = 0;
            while (i < pixels)
            {
                if (p >= len)
                    return false;
                int b = src[p++];
                if (b == 0xFE)
                {
                    if (len - p < 2)
                        return false;
                    px = (ushort)(src[p] | (src[p + 1] << 8));
                    p += 2;
                }
                else if ((b & 0xC0) == 0xC0)
                {
                    int run = (b & 0x3F) + 1;
                    if (run > pixels - i)
                        return false;
                    while (run-- > 0)
                        dst[i++] = px;
                    continue;
                }
                else if ((b & 0xC0) == 0x00)
                {
                    px = index[b];
                    dst[i++] = px;
                    continue;
                }
                else
                {
                    int dr, dg, db;
                    if ((b & 0xC0) == 0x40)
                    {
                        dr = ((b >> 4) & 3) - 2;
                        dg = ((b >> 2) & 3) - 2;
                        db = (b & 3) - 2;
                    }
                    else
                    {
                        if (p >= len)
                            return false;
                        int b2 = src[p++];
                        dg = (b & 0x3F) - 32;
                        dr = ((b2 >> 4) - 8) + (dg >> 1);
                        db = ((b2 & 0xF) - 8) + (dg >> 1);
                    }
                    int r = ((px >> 11) + dr) & 0x1F;
                    int g = (((px >> 5) & 0x3F) + dg) & 0x3F;
                    int bl = ((px & 0x1F) + db) & 0x1F;
                    px = (ushort)((r << 11) | (g << 5) | bl);
                }
                index[(int)(unchecked((uint)px * 0x9E3779B1u) >> 26)] = px;
                dst[i++] = px;
            }
            return p == len;
        }

        byte[] stored;      // (DecodeEntry's)

        // Reads entry 'e' as it's stored in the file (into 'buf', at least its Size long).
        public void ReadStored(int e, byte[] buf)
        {
            ReadAt(entries[e].Offset, buf, 0, entries[e].Size);
        }

        // Reads and decodes entry 'e' into 'dst' (Width * Height pixels, RGB565,
        // top row first).
        public void DecodeEntry(int e, ushort[] dst)
        {
            if (stored == null)
                stored = new byte[SlotBytes];
            ReadStored(e, stored);
            if (entries[e].Size == SlotBytes)
                Buffer.BlockCopy(stored, 0, dst, 0, SlotBytes);
            else if (!Decompress(stored, entries[e].Size, dst, Width * Height))
                throw new InvalidDataException($"frame {e} is damaged");
        }

        // The index entry shown at clip frame 'frame' (0 .. FrameCount - 1).
        public int EntryForFrame(int frame)
        {
            int lo = 0, hi = entries.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (entries[mid].FirstFrame <= frame)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        // Frames firstFrame .. firstFrame + frameCount - 1, in order: each stored
        // frame's pixels (RGB565, top row first; the same array each time) and
        // how many of those frames it's shown for.
        public IEnumerable<(ushort[] Pixels, int Copies)> Frames(int firstFrame, int frameCount)
        {
            ushort[] frame = new ushort[Width * Height];
            int end = firstFrame + frameCount;
            for (int e = EntryForFrame(firstFrame); e < entries.Length && entries[e].FirstFrame < end; e++)
            {
                DecodeEntry(e, frame);
                int from = Math.Max(entries[e].FirstFrame, firstFrame), to = Math.Min(entries[e].FirstFrame + entries[e].Copies, end);
                yield return (frame, to - from);
            }
        }

        public IEnumerable<(ushort[] Pixels, int Copies)> Frames() => Frames(0, FrameCount);

        // How many sound samples 'frames' frames last, rounded (flbAudioSamplesIn).
        public long AudioSamplesIn(int frames) => ((long)frames * AudioRateMhz / Fps + 500) / 1000;

        // How many sound samples into its entry's sound copy number 'k' of it
        // starts (each copy comes sampleRate / fps samples after the one before).
        public long AudioOffsetOf(int k) => (long)k * AudioRateMhz / ((long)Fps * 1000);

        // The sample at which clip frame 'frame' is first shown.
        public long AudioPosOf(int frame)
        {
            Entry e = entries[EntryForFrame(frame)];
            return e.AudioPos + AudioOffsetOf(frame - e.FirstFrame);
        }

        // Reads samples first .. first + count - 1 (stereo PCM16) into 'dst';
        // samples the clip doesn't hold are silence (flbReadAudio).
        public void ReadAudio(long first, int count, short[] dst)
        {
            Array.Clear(dst, 0, count * 2);
            if (!HasAudio)
                return;
            byte[] buf = new byte[count * 4];
            // The part of these samples the clip holds, in at most two pieces (the ring wraps).
            long a = Math.Max(first, audioFirst), b = Math.Min(first + count, audioEnd);
            while (a < b)
            {
                long pos = a % audioCapacity;
                long n = Math.Min(b - a, audioCapacity - pos);
                ReadAt(audioOffset + pos * 4, buf, (int)(a - first) * 4, (int)n * 4);
                a += n;
            }
            Buffer.BlockCopy(buf, 0, dst, 0, count * 4);
        }

        static void Put32(byte[] b, int o, long v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        static void Put16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        // Writes the sound of frames firstFrame .. firstFrame + frameCount - 1 to
        // a WAV file: from the sample at which the first of them is shown, as many
        // as they last. Samples the clip doesn't hold (from before recording
        // started, ...) are silence.
        public void WriteWav(string wav, int firstFrame, int frameCount)
        {
            long start = AudioPosOf(firstFrame), count = AudioSamplesIn(frameCount);
            long rate = (AudioRateMhz + 500) / 1000;
            byte[] header = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            Put32(header, 4, 36 + count * 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
            Put32(header, 16, 16);
            Put16(header, 20, 1);       // PCM
            Put16(header, 22, 2);       // stereo
            Put32(header, 24, rate);
            Put32(header, 28, rate * 4);
            Put16(header, 32, 4);
            Put16(header, 34, 16);
            Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            Put32(header, 40, count * 4);

            using (FileStream o = new FileStream(wav, FileMode.Create, FileAccess.Write))
            {
                o.Write(header, 0, header.Length);
                short[] samples = new short[65536 * 2];
                byte[] buf = new byte[65536 * 4];
                for (long done = 0; done < count; )
                {
                    int n = (int)Math.Min(count - done, 65536);
                    ReadAudio(start + done, n, samples);
                    Buffer.BlockCopy(samples, 0, buf, 0, n * 4);
                    o.Write(buf, 0, n * 4);
                    done += n;
                }
            }
        }

        public void WriteWav(string wav) => WriteWav(wav, 0, FrameCount);
    }
}
