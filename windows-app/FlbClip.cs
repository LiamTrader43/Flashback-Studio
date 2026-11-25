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
    // Clips with screens (VersionScreens in the version) are read here too. An
    // index entry of theirs holds what's new at one moment: the top screen's
    // frame, the bottom screen's (if the clip has one), or both; a screen it
    // has nothing for is as it was after the entry before. A frame is whole,
    // or what changed since the frame before it on the same screen (see
    // DecodeDeltaOps), so a screen's picture at an entry takes its frames from
    // its last whole one on: Show does that. Entries shown 0 times are only
    // read, for the frames after them.
    //
    // It also reads clips saved by older Flashback builds: raw RGB565 frames back
    // to back in a .raw, with a .txt next to it saying their resolution and frame
    // rate. They play and export like the others (as if each frame were an index
    // entry), but can't be compacted or trimmed.
    sealed class FlbClip : IFlbVideo, IDisposable
    {
        const uint Magic = 0x4B424C46;          // "FLBK"
        const uint InputMagic = 0x49424C46;     // "FLBI"
        public const int TrailerSize = 48;
        public const int AudioInfoSize = 32;
        public const int InputHeaderSize = 16;
        public const int InputRecordSize = 16;
        public const uint VersionCodes = 0x100;    // added to the version: frames may be packed with Huffman codes
        public const uint VersionScreens = 0x200;  // ...and: its entries hold what's new on each screen (see above)

        // How an entry's frames are stored (its Flags), in a clip with screens:
        // as what changed since the frame before, and packed with Huffman codes.
        public const int TopDelta = 1, TopCodes = 2, BottomDelta = 4, BottomCodes = 8;

        // A stored frame: where it is in the file, its size, how many times it's
        // shown, the clip frame it's first shown at, and (with sound) the sample
        // it's first shown at. In a clip with screens, Size is the top screen's
        // frame's (0: the top screen is as it was), and the rest applies.
        public struct Entry
        {
            public long Offset;
            public int Size, Copies, FirstFrame;
            public long AudioPos;
            public int BottomSize;      // the bottom screen's frame's size (0: it's as it was, or there's none)
            public int Flags;           // TopDelta, ...
            public int Missed;          // how many of Copies are missed frames (before screens: all but the first)

            // Where the bottom screen's frame is: after the top screen's, from the next multiple of 4.
            public long BottomOffset => Offset + ((Size + 3) & ~3);
            // How much the entry stores, from Offset.
            public int StoredSize => BottomSize != 0 ? ((Size + 3) & ~3) + BottomSize : Size;
        }

        public string Path { get; }
        public bool IsRaw { get; private set; }         // an older .raw clip
        public uint Version { get; private set; }       // 1: as recorded, 2: compacted, 3: with sound (either)
        public bool HasCodes { get; private set; }      // frames may be packed with Huffman codes (VersionCodes)
        public bool HasScreens { get; private set; }    // its entries hold what's new on each screen (VersionScreens)
        public int Width { get; private set; }          // the top screen's frames
        public int Height { get; private set; }
        public int BottomWidth { get; private set; }    // the bottom screen's (0: the clip has none)
        public int BottomHeight { get; private set; }
        public bool HasBottom => BottomWidth != 0;
        public int BottomBytes => BottomWidth * BottomHeight * 2;
        public int MissedFrames { get; private set; }   // frames that are copies because their capture was missed
        public int Fps { get; private set; }
        public long SlotStride { get; private set; }    // bytes between ring slots (0 once compacted)
        public int SlotBytes { get; private set; }      // the size of an uncompressed frame
        public int FrameCount { get; private set; }     // frames in the clip, copies included
        public long FileSize { get; private set; }
        public bool HasAudio { get; private set; }
        public long AudioRateMhz { get; private set; }  // the sound's sample rate, in millihertz
        public long AudioMissed { get; private set; }
        public long AudioCapacity => audioCapacity;     // samples its sound's ring has room for (sample i is at i % AudioCapacity)
        public FlbInput[] Inputs { get; private set; }  // the inputs during each frame, or null if the clip has none

        // Compacted already (version 2, or 3 without slots)?
        public bool IsCompacted => Version == 2 || (Version == 3 && SlotStride == 0);
        public int EntryCount => entries.Length;
        public Entry this[int e] => entries[e];
        // The sample at which the clip's first frame is shown (its first entry's,
        // or after entries that are only read, the first one shown).
        public long FirstAudioPos => entries[EntryForFrame(0)].AudioPos;

        readonly FileStream file;
        Entry[] entries;            // the stored frames, oldest first
        int[] topKey, bottomKey;    // (with screens) the last entry up to each with that screen's frame whole, or -1
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
            uint version = U32(t, 4) & ~(VersionCodes | VersionScreens);
            if (version < 1 || version > 3)
                throw new InvalidDataException("made by a newer Flashback version; get the latest Flashback Studio");
            bool screens = (U32(t, 4) & VersionScreens) != 0;
            int entrySize = screens ? 24 : version == 3 ? 16 : 12;
            int audioInfoSize = version == 3 ? AudioInfoSize : 0;

            uint width = U32(t, 8), height = U32(t, 12), fps = U32(t, 16);
            long stride = U32(t, 20);
            uint slotBytes = U32(t, 24), entryCount = U32(t, 28), frameCount = U32(t, 32);
            long indexOffset = U32(t, 36), inputOffset = U32(t, 40);
            // (With screens, the trailer's last word is the bottom screen's size, or 0.)
            uint bottomWidth = screens ? U32(t, 44) & 0xFFFF : 0, bottomHeight = screens ? U32(t, 44) >> 16 : 0;
            if (width == 0 || height == 0 || width > 400 || height > 240 || slotBytes != width * height * 2 ||
                fps == 0 || fps > 60 || (version == 1 && stride < slotBytes) || entryCount > length / 12 ||
                indexOffset + entryCount * entrySize + audioInfoSize + TrailerSize != length ||
                (bottomWidth == 0) != (bottomHeight == 0) || bottomWidth > 320 || bottomHeight > 240 ||
                bottomWidth > width || bottomHeight > height)
                throw new InvalidDataException("the clip's header is damaged");
            Version = version;
            HasCodes = (U32(t, 4) & VersionCodes) != 0;
            HasScreens = screens;
            BottomWidth = (int)bottomWidth;
            BottomHeight = (int)bottomHeight;
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
            long frames = 0, missedFrames = 0;
            for (int e = 0; e < entryCount && screens; e++)
            {
                // With screens: its offset, each screen's frame's size (the bottom
                // screen's after the top screen's), how many times it's shown (0:
                // only read), its sound, and how its frames are stored (the low 8
                // bits), with how many of its copies are missed frames above that
                // (all but the first, at most; as flb.c takes them).
                int p = e * entrySize;
                long offset = U32(index, p);
                uint top = U32(index, p + 4), bottom = U32(index, p + 8), copies = U32(index, p + 12), flags = U32(index, p + 20);
                uint missed = Math.Min(flags >> 16, copies == 0 ? 0 : copies - 1);
                long stored = bottom != 0 ? ((top + 3) & ~3u) + bottom : top;
                if (top > slotBytes || bottom > bottomWidth * bottomHeight * 2 || copies > frameCount - frames ||
                    offset + stored > indexOffset)
                    throw new InvalidDataException($"frame entry {e} is damaged");
                entries[e] = new Entry
                {
                    Offset = offset,
                    Size = (int)top,
                    BottomSize = (int)bottom,
                    Copies = (int)copies,
                    FirstFrame = (int)frames,
                    AudioPos = HasAudio ? U32(index, p + 16) : 0,
                    Flags = (int)(flags & 0xFF),
                    Missed = (int)missed,
                };
                frames += copies;
                missedFrames += missed;
            }
            for (int e = 0; e < entryCount && !screens; e++)
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
                    Missed = (int)copies - 1,
                };
                frames += copies;
                missedFrames += copies - 1;
            }
            if (frames != frameCount || entryCount == 0 || frameCount == 0)
                throw new InvalidDataException("the clip's frame count doesn't match its index");
            MissedFrames = (int)missedFrames;

            if (screens)
            {
                topKey = new int[entryCount];
                bottomKey = new int[entryCount];
                for (int e = 0, top = -1, bottom = -1; e < entryCount; e++)
                {
                    if (entries[e].Size != 0 && (entries[e].Flags & TopDelta) == 0)
                        top = e;
                    if (entries[e].BottomSize != 0 && (entries[e].Flags & BottomDelta) == 0)
                        bottom = e;
                    topKey[e] = top;
                    bottomKey[e] = bottom;
                }
            }

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
        // flbDecodeOps in flb.c). Returns false if the data is malformed.
        static bool DecodeOps(byte[] src, int len, ushort[] dst, int pixels)
        {
            ushort[] index = new ushort[64];
            ushort px = 0;
            int p = 0, i = 0;
            while (i < pixels)
            {
                if (p >= len)
                    return false;
                int b = src[p++];
                if (b == 0xFF)
                    return false;   // (SKIP: only in frames stored as what changed)
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

        // A frame stored as what changed since the frame before it on the same
        // screen (flashbackCompressDelta in flashback_compress.c): the same ops,
        // and SKIP (0xFF n: the next n + 1 pixels stay as they are, or 0xFF 0xFF
        // lo hi: the next (lo | hi << 8) + 1), after which the previous pixel
        // is the last one skipped. Its pixels come in the screen's own order
        // rather than upright: column by column, left to right, each from the
        // bottom row up. 'dst' (w x h, upright) holds the frame before, and
        // becomes this one. Returns false if the data is malformed (leaving
        // 'dst' part changed).
        static bool DecodeDeltaOps(byte[] src, int len, ushort[] dst, int w, int h)
        {
            ushort[] index = new ushort[64];
            ushort px = 0;
            int p = 0, i = 0, pixels = w * h;
            int at = (h - 1) * w, left = h;     // where the next pixel goes, and how many are left in its column
            while (i < pixels)
            {
                if (p >= len)
                    return false;
                int b = src[p++];
                if (b == 0xFF)
                {
                    if (p >= len)
                        return false;
                    int count = src[p++];
                    if (count == 0xFF)
                    {
                        if (len - p < 2)
                            return false;
                        count = src[p] | (src[p + 1] << 8);
                        p += 2;
                    }
                    count++;
                    if (count > pixels - i)
                        return false;
                    i += count;
                    px = dst[(h - 1 - (i - 1) % h) * w + (i - 1) / h];
                    at = (h - 1 - i % h) * w + i / h;
                    left = h - i % h;
                    continue;
                }
                int run = 1;
                if (b == 0xFE)
                {
                    if (len - p < 2)
                        return false;
                    px = (ushort)(src[p] | (src[p + 1] << 8));
                    p += 2;
                    index[(int)(unchecked((uint)px * 0x9E3779B1u) >> 26)] = px;
                }
                else if ((b & 0xC0) == 0xC0)
                {
                    run = (b & 0x3F) + 1;
                    if (run > pixels - i)
                        return false;
                }
                else if ((b & 0xC0) == 0x00)
                    px = index[b];
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
                    index[(int)(unchecked((uint)px * 0x9E3779B1u) >> 26)] = px;
                }
                for (i += run; run > 0; run--)
                {
                    dst[at] = px;
                    at -= w;
                    if (--left == 0)
                    {
                        // (On to the next column's bottom row.)
                        left = h;
                        at += h * w + 1;
                    }
                }
            }
            return p == len;
        }

        const int CodesHeader = 264;    // a frame packed with Huffman codes: bytes before the codes
        const int CodeBits = 12;        // the longest code

        ushort[][] codeTables;  // (DecodeCodes': each set's codes by their next CodeBits bits)
        byte[] codedBytes;      // (DecodeCodes': the compressed bytes the codes stand for)

        // Turns a frame packed with Huffman codes (see flashbackHuffman in
        // Luma3DS-Flashback's flashback_compress.c, and flbDecodeCodes in flb.c)
        // back into the compressed bytes they stand for, at most 'cap' of them,
        // into codedBytes; sets 'count'. Returns false if the data is malformed.
        bool DecodeCodes(byte[] src, int len, int cap, out int count)
        {
            count = 0;
            if (len < CodesHeader || src[0] != 0xFF || src[1] != 1)
                return false;
            uint n = U32(src, 4);
            if (n > cap)
                return false;
            if (codeTables == null)
            {
                codeTables = new[] { new ushort[1 << CodeBits], new ushort[1 << CodeBits] };
                codedBytes = new byte[Math.Max(SlotBytes, BottomBytes)];
            }

            // Each set's codes by their next CodeBits bits (first bit lowest): byte
            // value | code length << 8, or 0 where no code starts. The codes are
            // canonical: shorter first, and among the same length, lower values first.
            for (int set = 0; set < 2; set++)
            {
                ushort[] table = codeTables[set];
                int[] perLength = new int[CodeBits + 1], next = new int[CodeBits + 1];
                int LengthOf(int v) => src[8 + set * 128 + v / 2] >> (v & 1) * 4 & 0xF;
                for (int v = 0; v < 256; v++)
                {
                    if (LengthOf(v) > CodeBits)
                        return false;
                    perLength[LengthOf(v)]++;
                }
                int code = 0;
                perLength[0] = 0;
                for (int length = 1; length <= CodeBits; length++)
                {
                    code = (code + perLength[length - 1]) << 1;
                    next[length] = code;
                }
                Array.Clear(table, 0, table.Length);
                for (int v = 0; v < 256; v++)
                {
                    int length = LengthOf(v);
                    if (length == 0)
                        continue;
                    int c = next[length]++, reversed = 0;
                    if (c >= 1 << length)
                        return false;   // (more codes than there's room for)
                    for (int k = 0; k < length; k++)
                        reversed = reversed << 1 | (c >> k & 1);
                    for (int k = reversed; k < 1 << CodeBits; k += 1 << length)
                        table[k] = (ushort)(v | length << 8);
                }
            }

            // The bytes, each set's codes in turn: an op byte, then the bytes after
            // it (LUMA's second, RAW's two, SKIP's count: one, or three if the
            // first is 0xFF).
            int pos = CodesHeader, have = 0, after = 0;
            bool skipCount = false;     // the next byte is the first after a SKIP
            uint bits = 0;
            for (int i = 0; i < n; i++)
            {
                while (have <= 24)
                {
                    bits |= (uint)(pos < len ? src[pos] : 0) << have;
                    pos++;
                    have += 8;
                }
                int e = codeTables[after != 0 ? 1 : 0][bits & ((1 << CodeBits) - 1)], used = e >> 8;
                if (used == 0)
                    return false;
                bits >>= used;
                have -= used;
                byte b = (byte)e;
                codedBytes[i] = b;
                if (after != 0)
                {
                    after--;
                    if (skipCount && b == 0xFF)
                        after += 2;
                    skipCount = false;
                }
                else
                {
                    after = b >= 0x80 && b < 0xC0 ? 1 : b == 0xFE ? 2 : b == 0xFF ? 1 : 0;
                    skipCount = b == 0xFF;
                }
            }
            // (The codes must end in the frame's last 2 bytes: it's padded to 2.)
            long usedBits = (long)(pos - CodesHeader) * 8 - have, heldBits = (long)(len - CodesHeader) * 8;
            if (usedBits > heldBits || usedBits + 16 <= heldBits)
                return false;
            count = (int)n;
            return true;
        }

        // A stored frame (not stored raw), as flbDecompress in flb.c: its ops, or
        // (if it starts with 0xFF) its ops packed with Huffman codes.
        bool Decompress(byte[] src, int len, ushort[] dst, int pixels)
        {
            if (len != 0 && src[0] == 0xFF)
                return DecodeCodes(src, len, pixels * 2, out int count) && DecodeOps(codedBytes, count, dst, pixels);
            return DecodeOps(src, len, dst, pixels);
        }

        // A stored frame (not stored raw) of a w x h screen in a clip with
        // screens, as flbDecodeFrame in flb.c: packed with Huffman codes if
        // 'coded', and what changed since the frame before, which 'dst' holds,
        // if 'delta'.
        bool DecodeFrame(byte[] src, int len, ushort[] dst, int w, int h, bool delta, bool coded)
        {
            if (coded)
            {
                if (!DecodeCodes(src, len, w * h * 2, out int count))
                    return false;
                src = codedBytes;
                len = count;
            }
            return delta ? DecodeDeltaOps(src, len, dst, w, h) : DecodeOps(src, len, dst, w * h);
        }

        byte[] stored;      // (DecodeEntry's and Show's)

        // Reads entry 'e' as it's stored in the file, into 'buf' (at least its
        // StoredSize long): its frame, or in a clip with screens, the top
        // screen's, then zeros up to a multiple of 4 and the bottom screen's
        // (if it has both).
        public void ReadStored(int e, byte[] buf)
        {
            Entry entry = entries[e];
            ReadAt(entry.Offset, buf, 0, entry.Size);
            if (entry.BottomSize != 0)
            {
                int at = (entry.Size + 3) & ~3;
                Array.Clear(buf, entry.Size, at - entry.Size);
                ReadAt(entry.BottomOffset, buf, at, entry.BottomSize);
            }
        }

        Pictures seeking;   // (DecodeEntry's, in a clip with screens)

        // Reads and decodes entry 'e' into 'dst' (Width * Height pixels, RGB565,
        // top row first): in a clip with screens, the top screen's picture
        // after it (see Show).
        public void DecodeEntry(int e, ushort[] dst)
        {
            if (HasScreens)
            {
                if (seeking == null)
                    seeking = NewPictures(false);
                Show(seeking, e);
                Array.Copy(seeking.Top, dst, Width * Height);
                return;
            }
            if (stored == null)
                stored = new byte[SlotBytes];
            ReadStored(e, stored);
            if (entries[e].Size == SlotBytes)
                Buffer.BlockCopy(stored, 0, dst, 0, SlotBytes);
            else if (!Decompress(stored, entries[e].Size, dst, Width * Height))
                throw new InvalidDataException($"frame {e} is damaged");
        }

        // Both screens' pictures after an index entry (RGB565, top row first):
        // what a reader of the clip has on screen. Show moves it to another entry.
        public sealed class Pictures
        {
            public readonly ushort[] Top;       // Width x Height
            public readonly ushort[] Bottom;    // BottomWidth x BottomHeight; null without a bottom screen (or if it isn't wanted)
            public int Entry = -1;              // the entry they're after (-1: none yet)

            internal Pictures(FlbClip clip, bool bottom)
            {
                Top = new ushort[clip.Width * clip.Height];
                Bottom = bottom && clip.HasBottom ? new ushort[clip.BottomWidth * clip.BottomHeight] : null;
            }
        }

        // The last entry up to 'e' with a whole frame for the top screen (or the
        // bottom screen, if 'bottom'), in a clip with screens; -1 if there's none.
        public int WholeUpTo(int e, bool bottom) => bottom ? bottomKey[e] : topKey[e];

        // Pictures to Show entries in (without the bottom screen's, if 'bottom' is false).
        public Pictures NewPictures(bool bottom = true) => new Pictures(this, bottom);

        // Makes 'at' the pictures after entry 'e'. In a clip with screens, each
        // screen's frames are applied from where 'at' is, if that's before 'e'
        // and at or after the screen's last whole frame up to 'e'; otherwise
        // from that whole frame. A screen with no whole frame yet is black
        // (frames of what changed, before it, are ignored). If a frame is
        // damaged it throws, and 'at' is after no entry.
        public void Show(Pictures at, int e)
        {
            if (at.Entry == e)
                return;
            int from = at.Entry;
            at.Entry = -1;
            if (!HasScreens)
                DecodeEntry(e, at.Top);
            else
            {
                ShowScreen(at.Top, false, from, e);
                if (at.Bottom != null)
                    ShowScreen(at.Bottom, true, from, e);
            }
            at.Entry = e;
        }

        // One screen's picture after entry 'e', in 'dst', which holds it as it
        // is after entry 'from' (-1: nothing).
        void ShowScreen(ushort[] dst, bool bottom, int from, int e)
        {
            int key = bottom ? bottomKey[e] : topKey[e];
            if (key < 0)
            {
                Array.Clear(dst, 0, dst.Length);
                return;
            }
            int w = bottom ? BottomWidth : Width, h = bottom ? BottomHeight : Height;
            if (stored == null)
                stored = new byte[Math.Max(SlotBytes, BottomBytes)];
            for (int i = from >= key && from < e ? from + 1 : key; i <= e; i++)
            {
                Entry entry = entries[i];
                int size = bottom ? entry.BottomSize : entry.Size;
                if (size == 0)
                    continue;
                bool delta = (entry.Flags & (bottom ? BottomDelta : TopDelta)) != 0;
                bool coded = (entry.Flags & (bottom ? BottomCodes : TopCodes)) != 0;
                ReadAt(bottom ? entry.BottomOffset : entry.Offset, stored, 0, size);
                if (size == w * h * 2 && !delta)
                    Buffer.BlockCopy(stored, 0, dst, 0, size);
                else if (!DecodeFrame(stored, size, dst, w, h, delta, coded))
                    throw new InvalidDataException($"frame {i} is damaged");
            }
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
        // how many of those frames it's shown for. (The top screen's, in a clip
        // with screens: the same pixels again where only the bottom screen changed.)
        public IEnumerable<(ushort[] Pixels, int Copies)> Frames(int firstFrame, int frameCount)
        {
            foreach ((ushort[] top, ushort[] bottom, int copies) in Moments(firstFrame, frameCount, false))
                yield return (top, copies);
        }

        // The same, with the bottom screen's pixels too if 'bottom' (and the clip
        // has one; null otherwise): each index entry's pictures, the same arrays
        // each time.
        public IEnumerable<(ushort[] Top, ushort[] Bottom, int Copies)> Moments(int firstFrame, int frameCount, bool bottom = true)
        {
            Pictures at = NewPictures(bottom);
            int end = firstFrame + frameCount;
            for (int e = EntryForFrame(firstFrame); e < entries.Length && entries[e].FirstFrame < end; e++)
            {
                Show(at, e);
                int from = Math.Max(entries[e].FirstFrame, firstFrame), to = Math.Min(entries[e].FirstFrame + entries[e].Copies, end);
                if (to > from)
                    yield return (at.Top, at.Bottom, to - from);
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
            byte[] header = WavHeader(count, AudioRateMhz);
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

        // A WAV file's header, for 'count' stereo PCM16 samples at 'rateMhz' millihertz.
        public static byte[] WavHeader(long count, long rateMhz)
        {
            long rate = (rateMhz + 500) / 1000;
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
            return header;
        }
    }
}
