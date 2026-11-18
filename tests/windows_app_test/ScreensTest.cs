// The Windows app test's part for clips with screens (see WindowsAppTest.cs):
// clips whose entries hold what's new on each screen, the top one's frame, the
// bottom one's or both, whole or as what changed since the frame before
// (packed with Huffman codes or not), with entries shown many times (nothing
// changed, or frames were missed), entries that are only read before the first
// one shown, and a bottom screen whose first whole frame comes late.
//  - read back: every frame of both screens, in order, at random, and from part
//    way; the inputs, the sound, the missed frames; damaged ones;
//  - compacted and trimmed: read back again, checked against the rules (which
//    entries are kept, their copies and missed frames, zeros between an entry's
//    two frames), and against the 3DS app's bytes (compact_ref), if it reads
//    such clips;
//  - exported, with the bottom screen under the top one and without.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FlashbackStudio;

static partial class WindowsAppTest
{
    // An index entry of a clip with screens: what it stores, and what a reader shows after it.
    sealed class Shot
    {
        public byte[] Top, Bottom;          // each screen's frame as stored (null: nothing for it)
        public int Flags, Copies, Missed;
        public ushort[] TopNow, BottomNow;  // the pictures after it (BottomNow null: black, no whole frame yet)
    }

    sealed class ScreenSpec : Spec
    {
        public int BottomWidth = 6, BottomHeight = 6;   // (0: no bottom screen)
        public int Key = 5;                 // every Key-th entry has both screens whole
        public bool BottomLate;             // the bottom screen's first whole frame comes a few entries in
        public int[] Kinds = { 0, 1, 2, 3, 4, 5, 6 };   // how each new picture differs from the one before (see Changes)
        public List<Shot> Shots = new List<Shot>();
        public bool HasBottom => BottomWidth != 0;
        // Each frame's entry (the ones only read have none).
        public int[] ShotOfFrame => Shots.SelectMany((shot, i) => Enumerable.Repeat(i, shot.Copies)).ToArray();
    }

    // Whether a picture's pixel (x, y) is new in its version v, for each kind
    // of change: everything, one pixel, a box, every third row, the columns on
    // the right (whole columns the same, then none), scattered pixels, the
    // bottom half.
    static bool Changes(int kind, int v, int x, int y, int w, int h)
    {
        switch (kind)
        {
            case 0: return true;
            case 1: return x == v * 5 % w && y == v * 3 % h;
            case 2: return x >= w / 4 && x < w / 2 && y >= h / 4 && y < h / 2;
            case 3: return y % 3 == 0;
            case 4: return x >= w - w / 3;
            case 5: return (x * 7 + y * 13 + v) % 11 == 0;
            default: return y >= h / 2;
        }
    }

    // Version v of a screen's picture, from the one before (null: the first).
    static ushort[] NextPicture(ScreenSpec s, ushort[] before, int screen, int v, int w, int h)
    {
        ushort[] px = before != null ? (ushort[])before.Clone() : new ushort[w * h];
        int kind = s.Kinds[v % s.Kinds.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (before == null || Changes(kind, v, x, y, w, h))
                    px[y * w + x] = (ushort)(v * 977 + y * 263 + x / 4 * 31 + 5 + screen * 12345);
        return px;
    }

    // What follows an op byte (ops[i - 1] == b) in the recorder's compression:
    // LUMA's second byte, RAW's two, SKIP's count (one, or three if the first is 0xFF).
    static int After(byte[] ops, int i, byte b) =>
        b >= 0x80 && b < 0xC0 ? 1 : b == 0xFE ? 2 : b == 0xFF ? (ops[i] == 0xFF ? 3 : 1) : 0;

    // The recorder's compression of pixels in a row (flashbackPackPixels, and
    // flashbackCompressDelta in Luma3DS-Flashback's flashback_compress.c), all
    // its ops; with 'before', pixels that are as they were there are skipped
    // (SKIP), if the one after is too or the one before was.
    static byte[] CompressOps(ushort[] pixels, ushort[] before)
    {
        List<byte> o = new List<byte>();
        uint[] index = new uint[64];
        uint prev = 0;
        int run = 0, skip = 0, n = pixels.Length;
        void PutSkip()
        {
            for (; skip > 0x10000; skip -= 0x10000)
                o.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            if (skip > 255)
                o.AddRange(new byte[] { 0xFF, 0xFF, (byte)(skip - 1), (byte)((skip - 1) >> 8) });
            else
                o.AddRange(new byte[] { 0xFF, (byte)(skip - 1) });
            skip = 0;
        }
        for (int i = 0; i < n; i++)
        {
            uint px = pixels[i];
            if (before != null && px == before[i] && (skip != 0 || (i + 1 < n && pixels[i + 1] == before[i + 1])))
            {
                if (run != 0)
                {
                    o.Add((byte)(0xBF + run));
                    run = 0;
                }
                skip++;
                prev = px;
                continue;
            }
            if (skip != 0)
                PutSkip();
            if (px == prev)
            {
                if (++run == 62)
                {
                    o.Add(0xFD);
                    run = 0;
                }
                continue;
            }
            if (run != 0)
            {
                o.Add((byte)(0xBF + run));
                run = 0;
            }
            uint slot = unchecked(px * 0x9E3779B1u) >> 26;
            if (index[slot] == px)
            {
                o.Add((byte)slot);
                prev = px;
                continue;
            }
            index[slot] = px;
            int dr = unchecked((int)(((px >> 11) - (prev >> 11)) << 27)) >> 27;
            int dg = unchecked((int)(((px >> 5) - (prev >> 5)) << 26)) >> 26;
            int db = unchecked((int)((px - prev) << 27)) >> 27;
            if ((uint)((dr + 2) | (dg + 2) | (db + 2)) < 4)
                o.Add((byte)(0x40 + 0x2A + (dr << 4) + (dg << 2) + db));
            else
            {
                int tr = dr - (dg >> 1), tb = db - (dg >> 1);
                if ((uint)((tr + 8) | (tb + 8)) < 16)
                {
                    o.Add((byte)(0x80 + 32 + dg));
                    o.Add((byte)(0x88 + (tr << 4) + tb));
                }
                else
                {
                    o.Add(0xFE);
                    o.Add((byte)px);
                    o.Add((byte)(px >> 8));
                }
            }
            prev = px;
        }
        if (skip != 0)
            PutSkip();
        if (run != 0)
            o.Add((byte)(0xBF + run));
        return o.ToArray();
    }

    // An upright picture in the screen's own order: column by column, left to
    // right, each from the bottom row up (as frames of what changed are stored).
    static ushort[] Columns(ushort[] up, int w, int h)
    {
        ushort[] cols = new ushort[w * h];
        for (int x = 0; x < w; x++)
            for (int j = 0; j < h; j++)
                cols[x * h + j] = up[(h - 1 - j) * w + x];
        return cols;
    }

    // A screen's frame as stored: what changed since 'before' (if it's given),
    // or the whole picture (raw, or compressed with a few of the ops or all of
    // them), packed with codes now and then (if the clip may have them, and
    // that's smaller). Its flags are TopDelta and TopCodes (the top screen's).
    static byte[] StoreFrame(ScreenSpec s, Random rng, ushort[] picture, ushort[] before, int w, int h, out int flags)
    {
        int raw = w * h * 2;
        byte[] ops = null;
        flags = 0;
        if (before != null)
        {
            ops = CompressOps(Columns(picture, w, h), Columns(before, w, h));
            if (ops.Length < raw)
                flags = FlbClip.TopDelta;
            else
                ops = null;
        }
        if (ops == null)
        {
            int how = rng.Next(4);
            ops = how == 0 ? null : how == 1 ? Compress(picture) : CompressOps(picture, null);
            if (ops == null || ops.Length >= raw)
            {
                byte[] bytes = new byte[raw];
                Buffer.BlockCopy(picture, 0, bytes, 0, raw);
                return bytes;
            }
        }
        if (s.Codes && rng.Next(2) == 0)
        {
            byte[] coded = PackWithCodes(ops);
            if (coded.Length < ops.Length)
            {
                flags |= FlbClip.TopCodes;
                return coded;
            }
        }
        return ops;
    }

    // Makes a clip's entries from 'slots' grid slots, as the recorder would:
    // each screen's picture changes now and then, a capture is missed now and
    // then, an entry is made when something's new (the top screen's frame, the
    // bottom screen's, or both), and every s.Key-th entry has both whole. The
    // bottom screen's frame is left out now and then (no room for it), which
    // leaves it as it was until its next one: whole, or what changed since the
    // last one stored. Now and then a screen's whole frame is in an entry of
    // its own, shown for no time, before the entry with the other screen's
    // frame of the same moment (no room for both in one). With s.BottomLate, the
    // first three entries only have frames of what changed for it (to ignore).
    // Then the entries before 'firstShown' are only read (from the last one
    // with whole frames on; the ones before that are dropped).
    static void Build(ScreenSpec s, Random rng, int slots, int firstShown = 0)
    {
        int w = s.Width, h = s.Height, bw = s.BottomWidth, bh = s.BottomHeight;
        ushort[] top = null, bottom = null;     // the screens now
        ushort[] topStored = null, bottomStored = null, bottomShown = null;     // what a reader has (the bottom one's: after a whole frame)
        int topVersion = 0, bottomVersion = 0;
        bool bottomWhole = true;                // the bottom screen's next frame must be whole
        s.Shots.Clear();
        for (int t = 0; t < slots; t++)
        {
            Shot last = s.Shots.LastOrDefault();
            if (t > 0 && rng.Next(9) == 0)
            {
                last.Copies++;      // a missed capture
                last.Missed++;
                continue;
            }
            if (t == 0 || rng.Next(3) != 0)
                top = NextPicture(s, top, 0, topVersion++, w, h);
            if (s.HasBottom && (t == 0 || rng.Next(4) == 0))
                bottom = NextPicture(s, bottom, 1, bottomVersion++, bw, bh);
            bool key = s.Shots.Count % s.Key == 0, late = s.HasBottom && s.BottomLate && s.Shots.Count < 3;
            bool topNew = top != topStored, bottomNew = s.HasBottom && (bottom != bottomStored || (bottomWhole && !late));
            if (!key && !topNew && !bottomNew)
            {
                last.Copies++;      // nothing changed
                continue;
            }
            Shot shot = new Shot { Copies = 1 };
            ushort[] topBefore = topStored, bottomBefore = bottomShown;
            if (key || topNew)
            {
                shot.Top = StoreFrame(s, rng, top, key || rng.Next(4) == 0 ? null : topStored, w, h, out shot.Flags);
                topStored = top;
            }
            if (late && (key || bottomNew))
            {
                // (What changed since a picture no reader has: all but a pixel of it the same.)
                ushort[] other = (ushort[])bottom.Clone();
                other[t % other.Length] ^= 0x5555;
                shot.Bottom = CompressOps(Columns(bottom, bw, bh), Columns(other, bw, bh));
                shot.Flags |= FlbClip.BottomDelta;
                bottomStored = bottom;
            }
            else if (s.HasBottom && (key || bottomNew))
            {
                if (!key && !bottomWhole && rng.Next(6) == 0)
                    bottomWhole = rng.Next(2) == 0;     // (no room for it this time: the next one whole, or not)
                else
                {
                    bool whole = key || bottomWhole || rng.Next(4) == 0;
                    shot.Bottom = StoreFrame(s, rng, bottom, whole ? null : bottomShown, bw, bh, out int flags);
                    shot.Flags |= flags << 2;   // (BottomDelta, BottomCodes)
                    bottomShown = bottom;
                    bottomWhole = false;
                }
                bottomStored = bottom;
            }
            if (shot.Top == null && shot.Bottom == null)
            {
                last.Copies++;      // (only the bottom screen's frame, and it was left out)
                continue;
            }
            if (s.Shots.Count > 0 && shot.Top != null && shot.Bottom != null && (shot.Flags & FlbClip.BottomDelta) == 0 && rng.Next(3) == 0)
            {
                // (The bottom screen's whole frame in an entry of its own, only read, before the top screen's.)
                s.Shots.Add(new Shot { Bottom = shot.Bottom, Flags = shot.Flags & FlbClip.BottomCodes, TopNow = topBefore, BottomNow = bottomShown });
                shot.Bottom = null;
                shot.Flags &= FlbClip.TopDelta | FlbClip.TopCodes;
            }
            else if (s.Shots.Count > 0 && shot.Top != null && shot.Bottom != null && (shot.Flags & FlbClip.TopDelta) == 0 && rng.Next(3) == 0)
            {
                // (Or the top screen's, before the bottom screen's: as the recorder stores them with the bottom screen first.)
                s.Shots.Add(new Shot { Top = shot.Top, Flags = shot.Flags & FlbClip.TopCodes, TopNow = topStored, BottomNow = bottomBefore });
                shot.Top = null;
                shot.Flags &= FlbClip.BottomDelta | FlbClip.BottomCodes;
            }
            shot.TopNow = topStored;
            shot.BottomNow = bottomShown;
            s.Shots.Add(shot);
        }

        if (firstShown > 0)
        {
            firstShown = Math.Min(firstShown, s.Shots.Count - 1);
            int from = firstShown - firstShown % s.Key;
            s.Shots.RemoveRange(0, from);
            for (int i = 0; i < firstShown - from; i++)
                s.Shots[i].Copies = s.Shots[i].Missed = 0;
            // (A screen is black until its first whole frame in what's left: the first entry may be
            // the other screen's alone.)
            for (int i = 0; i < s.Shots.Count && (s.Shots[i].Top == null || (s.Shots[i].Flags & FlbClip.TopDelta) != 0); i++)
                s.Shots[i].TopNow = null;
            for (int i = 0; i < s.Shots.Count && (s.Shots[i].Bottom == null || (s.Shots[i].Flags & FlbClip.BottomDelta) != 0); i++)
                s.Shots[i].BottomNow = null;
            // (And the first one shown, from part way through its copies.)
            Shot first = s.Shots[firstShown - from];
            if (first.Copies > 1)
            {
                first.Copies--;
                first.Missed = Math.Min(first.Missed, first.Copies - 1);
            }
        }
        s.Copies = s.Shots.Select(shot => shot.Copies).ToArray();
    }

    // Writes a clip with screens as 's' says (its Shots); returns where its index starts.
    static long WriteScreens(string path, ScreenSpec s)
    {
        int entries = s.Shots.Count, slotBytes = s.Width * s.Height * 2, bottomBytes = s.BottomWidth * s.BottomHeight * 2;
        int stride = ((slotBytes + 3) & ~3) + bottomBytes + SlotPad;
        int slots = entries + 3, firstSlot = entries / 2;      // (recorded: the ring wraps)
        if (s.Audio)
        {
            if (s.Compacted)
            {
                s.Capacity = s.TrackLength;
                s.TrackStart = s.First = 5000;
                s.End = s.First + s.TrackLength;
            }
            else
            {
                s.Capacity = s.TrackLength + 50;
                s.First = 3 * s.Capacity + 17;
                s.TrackStart = s.First - 20;
                s.End = s.TrackStart + s.TrackLength - 30;
            }
        }

        using (FileStream f = new FileStream(path, FileMode.Create))
        {
            long audioOffset = 0;
            long[] offsets = new long[entries];
            void WriteAudio()
            {
                audioOffset = f.Position;
                for (long p = 0; p < s.Capacity; p++)
                {
                    Put16(f, 0x7777);   // (what isn't held)
                    Put16(f, 0x7777);
                }
                for (long i = s.First; i < s.End; i++)
                {
                    f.Position = audioOffset + i % s.Capacity * 4;
                    Put16(f, Left(i));
                    Put16(f, Right(i));
                }
                f.Position = audioOffset + s.Capacity * 4;
            }
            // An entry's frames: the top screen's, then the bottom screen's from the next multiple of 4.
            void WriteShot(Shot shot)
            {
                long at = f.Position;
                if (shot.Top != null)
                    f.Write(shot.Top, 0, shot.Top.Length);
                if (shot.Bottom != null)
                {
                    long to = at + (((shot.Top?.Length ?? 0) + 3) & ~3);
                    if (s.Compacted)
                        while (f.Position < to)
                            f.WriteByte(0);
                    f.Position = to;
                    f.Write(shot.Bottom, 0, shot.Bottom.Length);
                }
            }

            if (s.Audio && !s.Compacted)
            {
                WriteAudio();
                f.Position = (f.Position + 511) / 512 * 512;
            }
            long videoOffset = f.Position;
            if (s.Compacted)
                for (int e = 0; e < entries; e++)
                {
                    offsets[e] = f.Position;
                    WriteShot(s.Shots[e]);
                }
            else
            {
                // (The ring's slots hold something else, where entries don't: old frames.)
                byte[] old = Enumerable.Repeat((byte)0xEE, stride).ToArray();
                for (int i = 0; i < slots; i++)
                    f.Write(old, 0, stride);
                for (int e = 0; e < entries; e++)
                {
                    offsets[e] = videoOffset + (long)(firstSlot + e) % slots * stride;
                    f.Position = offsets[e];
                    WriteShot(s.Shots[e]);
                }
                f.Position = videoOffset + (long)slots * stride;
            }
            if (s.Audio && s.Compacted)
                WriteAudio();

            long inputOffset = 0;
            if (s.Inputs)
            {
                inputOffset = f.Position;
                Put32(f, 0x49424C46);
                Put32(f, 1);
                Put32(f, s.Frames);
                Put32(f, 16);
                for (int i = 0; i < s.Frames; i++)
                {
                    FlbInput n = InputOf(i);
                    Put32(f, n.Buttons);
                    Put16(f, n.CircleX);
                    Put16(f, n.CircleY);
                    Put16(f, n.TouchX);
                    Put16(f, n.TouchY);
                    Put16(f, n.CStickX);
                    Put16(f, n.CStickY);
                }
            }

            long indexOffset = f.Position;
            int frame = 0;
            for (int e = 0; e < entries; e++)
            {
                Shot shot = s.Shots[e];
                Put32(f, offsets[e]);
                Put32(f, shot.Top?.Length ?? 0);
                Put32(f, shot.Bottom?.Length ?? 0);
                Put32(f, shot.Copies);
                Put32(f, s.Audio && shot.Copies != 0 ? s.TrackStart + frame * RateMhz / (Fps * 1000) : 0);
                Put32(f, (uint)shot.Flags | (uint)shot.Missed << 16);
                frame += shot.Copies;
            }
            if (s.Audio)
            {
                Put32(f, RateMhz);
                Put16(f, 2);
                Put16(f, 16);
                Put32(f, audioOffset);
                Put32(f, s.Capacity);
                Put32(f, s.First);
                Put32(f, s.End);
                Put32(f, 0);
                Put32(f, 0);
            }
            foreach (long v in new long[] { 0x4B424C46, (uint)s.Version | (s.Codes ? FlbClip.VersionCodes : 0) | FlbClip.VersionScreens, s.Width, s.Height, Fps,
                                            s.Compacted ? 0 : stride, slotBytes, entries, s.Frames, indexOffset, inputOffset,
                                            (uint)s.BottomWidth | (uint)s.BottomHeight << 16 })
                Put32(f, v);
            return indexOffset;
        }
    }

    static bool SamePicture(ushort[] got, ushort[] want) =>
        want == null ? got == null || got.All(p => p == 0) : got != null && got.SequenceEqual(want);

    static string Describe(ScreenSpec s) =>
        $"{s.Width}x{s.Height}{(s.HasBottom ? $" and {s.BottomWidth}x{s.BottomHeight}" : "")}, {s.Shots.Count} entries " +
        $"({s.Shots.Count(shot => shot.Copies == 0)} only read), {s.Frames} frames{(s.Compacted ? ", compacted" : "")}{(s.Audio ? ", sound" : "")}" +
        $"{(s.Inputs ? ", inputs" : "")}{(s.Codes ? ", codes" : "")}{(s.BottomLate ? ", the bottom screen late" : "")}";

    // Reads a clip with screens back: its frames in order (both screens', and
    // the top screen's alone), at random, and its entries, inputs and sound.
    // 'shots' are the entries it should have, 'firstInput' the first frame's inputs.
    static void CheckScreens(string what, string path, ScreenSpec s, List<Shot> shots, bool inputs, Random rng, int firstInput = 0, bool sound = true)
    {
        checks++;
        try
        {
            using (FlbClip clip = FlbClip.Open(path))
            {
                int frames = shots.Sum(shot => shot.Copies), missed = shots.Sum(shot => shot.Missed);
                if (!clip.HasScreens || clip.Width != s.Width || clip.Height != s.Height || clip.BottomWidth != s.BottomWidth ||
                    clip.BottomHeight != s.BottomHeight || clip.HasBottom != s.HasBottom || clip.Fps != Fps)
                    Fail($"{what}: {clip.Width}x{clip.Height} and {clip.BottomWidth}x{clip.BottomHeight}, {clip.Fps} fps");
                if (clip.FrameCount != frames || clip.EntryCount != shots.Count || clip.MissedFrames != missed)
                    Fail($"{what}: {clip.FrameCount} frames, {clip.EntryCount} entries, {clip.MissedFrames} missed; want {frames}, {shots.Count}, {missed}");
                if (clip.HasAudio != s.Audio || clip.HasCodes != s.Codes)
                    Fail($"{what}: sound {clip.HasAudio}, codes {clip.HasCodes}");
                if ((clip.Inputs != null) != inputs)
                    Fail($"{what}: inputs {(inputs ? "missing" : "where there were none")}");
                else if (inputs && !Enumerable.Range(0, frames).All(i => clip.Inputs[i].Equals(InputOf(firstInput + i))))
                    Fail($"{what}: the wrong inputs");
                for (int e = 0; e < Math.Min(shots.Count, clip.EntryCount); e++)
                    if (clip[e].Copies != shots[e].Copies || clip[e].Missed != shots[e].Missed || clip[e].Flags != shots[e].Flags ||
                        clip[e].Size != (shots[e].Top?.Length ?? 0) || clip[e].BottomSize != (shots[e].Bottom?.Length ?? 0))
                    {
                        Fail($"{what}: entry {e} is {clip[e].Size}+{clip[e].BottomSize} bytes, flags {clip[e].Flags}, shown {clip[e].Copies} times, {clip[e].Missed} missed");
                        break;
                    }

                // In order: each entry shown, both screens' pictures.
                List<Shot> shown = shots.Where(shot => shot.Copies != 0).ToList();
                int n = 0;
                foreach ((ushort[] top, ushort[] bottom, int copies) in clip.Moments(0, clip.FrameCount))
                {
                    if (n >= shown.Count || copies != shown[n].Copies)
                    {
                        Fail($"{what}: shown entry {n} is shown {copies} times");
                        break;
                    }
                    if (!SamePicture(top, shown[n].TopNow) || (s.HasBottom ? !SamePicture(bottom, shown[n].BottomNow) : bottom != null))
                    {
                        Fail($"{what}: shown entry {n} shows the wrong picture");
                        break;
                    }
                    n++;
                }
                if (n != shown.Count)
                    Fail($"{what}: {n} entries shown, of {shown.Count}");
                // The top screen alone (as exports without the bottom one read it).
                n = 0;
                foreach ((ushort[] px, int copies) in clip.Frames())
                {
                    if (n >= shown.Count || copies != shown[n].Copies || !SamePicture(px, shown[n].TopNow))
                    {
                        Fail($"{what}: the top screen's frames: shown entry {n} is wrong");
                        break;
                    }
                    n++;
                }

                // From part way, and frame by frame: the entry each frame is in.
                int[] shotOf = shots.SelectMany((shot, i) => Enumerable.Repeat(i, shot.Copies)).ToArray();
                for (int f = 0; f < frames; f++)
                    if (clip.EntryForFrame(f) != shotOf[f])
                    {
                        Fail($"{what}: frame {f} is in entry {clip.EntryForFrame(f)}, not {shotOf[f]}");
                        break;
                    }
                for (int t = 0; t < 4; t++)
                {
                    int first = rng.Next(frames), count = 1 + rng.Next(frames - first), f = first;
                    foreach ((ushort[] top, ushort[] bottom, int copies) in clip.Moments(first, count))
                    {
                        Shot want = shots[shotOf[f]];
                        if (!SamePicture(top, want.TopNow) || (s.HasBottom && !SamePicture(bottom, want.BottomNow)) ||
                            Enumerable.Range(f, copies).Any(g => g >= first + count || shotOf[g] != shotOf[f]))
                        {
                            Fail($"{what}: frames {first}+{count}: frame {f} is wrong");
                            break;
                        }
                        f += copies;
                    }
                    if (f != first + count)
                        Fail($"{what}: frames {first}+{count}: read up to {f}");
                }

                // At random: on a step, back, far ahead; entries that are only read too.
                FlbClip.Pictures at = clip.NewPictures();
                ushort[] one = new ushort[s.Width * s.Height];
                for (int t = 0, e = 0; t < 60; t++)
                {
                    e = rng.Next(3) == 0 ? Math.Min(e + 1, shots.Count - 1) : rng.Next(shots.Count);
                    clip.Show(at, e);
                    if (at.Entry != e || !SamePicture(at.Top, shots[e].TopNow) || (s.HasBottom && !SamePicture(at.Bottom, shots[e].BottomNow)))
                    {
                        Fail($"{what}: entry {e}, gone to from another, shows the wrong picture");
                        break;
                    }
                    if (t % 7 == 0)
                    {
                        clip.DecodeEntry(e, one);
                        if (!SamePicture(one, shots[e].TopNow))
                        {
                            Fail($"{what}: entry {e} decoded on its own is wrong");
                            break;
                        }
                    }
                }

                if (s.Audio && sound)
                {
                    // (The first entry shown's sound, whatever the ones only read before it say.)
                    if (clip.FirstAudioPos != s.TrackStart)
                        Fail($"{what}: its sound starts at {clip.FirstAudioPos}, not {s.TrackStart}");
                    string wav = path + ".wav";
                    clip.WriteWav(wav);
                    CheckWav(what, wav, s, 0, s.Frames);
                    int first = s.Frames / 3;
                    clip.WriteWav(wav, first, s.Frames - first);
                    CheckWav(what + ", from frame " + first, wav, s, first, s.Frames - first);
                    File.Delete(wav);
                }
            }
        }
        catch (InvalidDataException x)
        {
            Fail($"{what}: {x.Message}");
        }
    }

    // Some clips with screens of every sort.
    static IEnumerable<ScreenSpec> ScreenSpecs(Random rng, int count)
    {
        for (int run = 0; run < count; run++)
        {
            ScreenSpec s = new ScreenSpec
            {
                Audio = run % 3 != 0,
                Inputs = run % 4 != 1,
                Compacted = run % 2 == 1,
                Codes = run % 5 < 3,
                BottomLate = run % 6 == 4 || run % 12 == 2,
                Key = 2 + run % 5,
            };
            if (run % 4 == 3)
                s.BottomWidth = s.BottomHeight = 0;     // (the top screen alone)
            else if (run % 3 == 1)
            {
                s.Width = 20;       // (odd sizes: frames end off multiples of 4)
                s.Height = 9;
                s.BottomWidth = 13;
                s.BottomHeight = 7;
            }
            if (run % 8 == 5)
            {
                s.Width = 400;      // (the real sizes: long SKIPs, and frames big enough for codes)
                s.Height = 240;
                s.BottomWidth = 320;
                s.BottomHeight = 240;
            }
            Build(s, rng, s.Width == 400 ? 14 : 4 + rng.Next(70), run % 3 == 2 ? 1 + rng.Next(9) : 0);
            yield return s;
        }
    }

    static void TestScreens()
    {
        Random rng = new Random(2026);
        int deltas = 0, coded = 0, lead = 0, bottoms = 0, longSkips = 0;
        foreach (ScreenSpec s in ScreenSpecs(rng, 60))
        {
            WriteScreens("screens.flb", s);
            CheckScreens("screens: " + Describe(s), "screens.flb", s, s.Shots, s.Inputs, rng);
            deltas += s.Shots.Count(shot => (shot.Flags & (FlbClip.TopDelta | FlbClip.BottomDelta)) != 0);
            coded += s.Shots.Count(shot => (shot.Flags & (FlbClip.TopCodes | FlbClip.BottomCodes)) != 0);
            lead += s.Shots.Count(shot => shot.Copies == 0);
            bottoms += s.Shots.Count(shot => shot.Bottom != null);
            longSkips += s.Shots.Where(shot => shot.Top != null && (shot.Flags & (FlbClip.TopDelta | FlbClip.TopCodes)) == FlbClip.TopDelta)
                                .Count(shot => Enumerable.Range(0, shot.Top.Length - 1).Any(i => shot.Top[i] == 0xFF && shot.Top[i + 1] == 0xFF));
        }
        Console.WriteLine($"  with screens: {deltas} entries with frames of what changed, {coded} packed with codes, {lead} only read, " +
                          $"{bottoms} with the bottom screen's frame, {longSkips} with long SKIPs");
        if (deltas == 0 || coded == 0 || lead == 0 || bottoms == 0 || longSkips == 0)
            Fail("clips with screens: not every case came up");

        // Damaged ones. (Entries are 24 bytes: offset, the top screen's size, the
        // bottom screen's, copies, sound, flags with the missed frames above.)
        ScreenSpec d = new ScreenSpec { Width = 400, Height = 240, BottomWidth = 320, BottomHeight = 240, Audio = true, Inputs = true, Codes = true,
                                        Key = 4, Kinds = new[] { 0, 2, 6, 3 } };
        // (A frame of what changed that starts with pixels that stay: read as a whole frame, it's damaged.)
        bool StaysFirst(Shot shot) => shot.Flags == FlbClip.TopDelta && shot.Copies > 1 && shot.Top[0] == 0xFF;
        do
            Build(d, rng, 16);
        while (d.Shots.Count < 6 || !d.Shots.Skip(1).Any(StaysFirst) ||
               !d.Shots.Skip(1).Any(shot => shot.Bottom != null && shot.Bottom.Length != 320 * 240 * 2));
        long index = WriteScreens("good_screens.flb", d);
        CheckScreens("screens, at the real sizes", "good_screens.flb", d, d.Shots, true, rng);
        // (With 'topReads', the top screen's frames alone must still read.)
        void Damaged(string what, Action<string> damage, string message, bool topReads = false)
        {
            File.Copy("good_screens.flb", "bad.flb", true);
            damage("bad.flb");
            checks++;
            try
            {
                using (FlbClip clip = FlbClip.Open("bad.flb"))
                {
                    if (topReads)
                        foreach ((ushort[] px, int copies) in clip.Frames())
                        {
                        }
                    foreach ((ushort[] top, ushort[] bottom, int copies) in clip.Moments(0, clip.FrameCount))
                    {
                    }
                }
                Fail($"screens, {what}: read without complaint");
            }
            catch (InvalidDataException x)
            {
                if (x.Message != message)
                    Fail($"screens, {what}: \"{x.Message}\", want \"{message}\"");
            }
        }
        int delta = d.Shots.FindIndex(1, StaysFirst);
        int both = d.Shots.FindIndex(1, shot => shot.Bottom != null && shot.Bottom.Length != 320 * 240 * 2);
        Damaged("from a newer version", p => Patch32(p, -44, 3 | 0x700), "made by a newer Flashback version; get the latest Flashback Studio");
        Damaged("a bottom screen wider than the top one", p => Patch32(p, -4, 401 | 240 << 16), "the clip's header is damaged");
        Damaged("a bottom screen with no height", p => Patch32(p, -4, 320), "the clip's header is damaged");
        Damaged("the old entries' size", p => Patch32(p, -44, 3 | 0x100), "the clip's header is damaged");
        Damaged("a bottom screen bigger than a 3DS's", p => { Patch32(p, -40, 400); Patch32(p, -4, 321 | 240 << 16); }, "the clip's header is damaged");
        Damaged("a bottom screen's frame too big", p => Patch32(p, index + both * 24 + 8, 320 * 240 * 2 + 1), $"frame entry {both} is damaged");
        Damaged("frames miscounted", p => Patch32(p, -16, d.Frames + 1), "the clip's frame count doesn't match its index");
        Damaged("a frame of what changed cut short", p => Patch32(p, index + delta * 24 + 4, d.Shots[delta].Top.Length - 1), $"frame {delta} is damaged");
        Damaged("a frame of what changed marked whole", p => Patch32(p, index + delta * 24 + 20, 0), $"frame {delta} is damaged");
        Damaged("the bottom screen's frame cut short (the top screen's still read)",
                p => Patch32(p, index + both * 24 + 8, d.Shots[both].Bottom.Length - 1), $"frame {both} is damaged", true);

        // More missed frames than an entry can have (all its copies but the
        // first; none, for one that's only read), and flags above the low 8
        // bits: read as the most it can have, and without them.
        File.Copy("good_screens.flb", "odd.flb", true);
        Patch32("odd.flb", index + delta * 24 + 20, FlbClip.TopDelta | 0x4500 | 500 << 16);
        Patch32("odd.flb", index + 12, 0);
        Patch32("odd.flb", index + 20, 7 << 16);
        Patch32("odd.flb", -16, d.Frames - d.Shots[0].Copies);
        checks++;
        try
        {
            using (FlbClip clip = FlbClip.Open("odd.flb"))
                if (clip[delta].Missed != d.Shots[delta].Copies - 1 || clip[delta].Flags != FlbClip.TopDelta || clip[0].Missed != 0 ||
                    clip[0].Copies != 0 || clip.FrameCount != d.Frames - d.Shots[0].Copies)
                    Fail($"screens, too many missed frames: entry {delta} has {clip[delta].Missed} (flags {clip[delta].Flags}), entry 0 {clip[0].Missed}");
        }
        catch (InvalidDataException x)
        {
            Fail($"screens, too many missed frames: {x.Message}");
        }
    }

    // ---- Compacting and trimming clips with screens

    // Checks a compacted or trimmed clip with screens against its rules: the
    // bytes between an entry's two frames are zeros, and nothing lies between
    // one entry's data and the next's.
    static void CheckPacked(string what, string path)
    {
        byte[] b = File.ReadAllBytes(path);
        using (FlbClip clip = FlbClip.Open(path))
        {
            long at = clip[0].Offset;
            for (int e = 0; e < clip.EntryCount; e++)
            {
                FlbClip.Entry entry = clip[e];
                if (entry.Offset != at)
                {
                    Fail($"{what}: entry {e} is at {entry.Offset}, not {at}");
                    return;
                }
                if (entry.BottomSize != 0)
                    for (long i = entry.Offset + entry.Size; i < entry.BottomOffset; i++)
                        if (b[i] != 0)
                        {
                            Fail($"{what}: entry {e} has something between its two frames");
                            return;
                        }
                at += entry.StoredSize;
            }
            if (!clip.IsCompacted)
                Fail($"{what}: not compacted");
        }
    }

    static void TestScreensCompactor(string compactRef)
    {
        Random rng = new Random(77);
        // Does the 3DS app's compactor read clips with screens yet?
        ScreenSpec probe = new ScreenSpec();
        Build(probe, rng, 6);
        WriteScreens("probe.flb", probe);
        string probed = Ref(compactRef, "compact probe.flb");
        bool theirs = probed == null;
        if (!theirs)
            Console.WriteLine($"  the 3DS app's compactor doesn't take clips with screens (\"{probed}\"): their bytes weren't compared with it");
        int compared = 0, trims = 0, leads = 0;

        foreach (ScreenSpec s in ScreenSpecs(rng, 36))
        {
            if (s.Compacted)
                continue;
            string what = Describe(s);
            WriteScreens("sorig.flb", s);
            File.Copy("sorig.flb", "smine.flb", true);
            string mine = Mine(() => FlbCompactor.Compact("smine.flb"));
            if (mine != null)
            {
                Fail($"compacting screens, {what}: {mine}");
                continue;
            }
            // Every entry kept, as it was; read back like the clip it was made from.
            using (FlbClip was = FlbClip.Open("sorig.flb"))
                if (new FileInfo("smine.flb").Length != FlbCompactor.CompactSize(was))
                    Fail($"compacting screens, {what}: {new FileInfo("smine.flb").Length} bytes, not the {FlbCompactor.CompactSize(was)} expected");
            ScreenSpec c = new ScreenSpec { Width = s.Width, Height = s.Height, BottomWidth = s.BottomWidth, BottomHeight = s.BottomHeight,
                                            Audio = s.Audio, Inputs = s.Inputs, Codes = s.Codes, Compacted = true, Copies = s.Copies };
            CheckScreens("compacted screens, " + what, "smine.flb", c, s.Shots, s.Inputs, rng, sound: false);
            CheckPacked("compacted screens, " + what, "smine.flb");
            if (s.Audio)
            {
                using (FlbClip a = FlbClip.Open("sorig.flb"))
                    a.WriteWav("swant.wav");
                using (FlbClip a = FlbClip.Open("smine.flb"))
                    a.WriteWav("sgot.wav");
                if (!File.ReadAllBytes("swant.wav").SequenceEqual(File.ReadAllBytes("sgot.wav")))
                    Fail($"compacting screens, {what}: its sound changed");
            }
            if (theirs)
            {
                File.Copy("sorig.flb", "stheirs.flb", true);
                string them = Ref(compactRef, "compact stheirs.flb");
                if (them != null)
                    Fail($"compacting screens, {what}: the 3DS app: {them}");
                else
                {
                    SameBytes("compacting screens, " + what, "smine.flb", "stheirs.flb");
                    compared++;
                }
            }

            // Trimmed, from the recorded clip and from the compacted one.
            int[] shotOf = s.ShotOfFrame;
            for (int t = 0; t < 6; t++)
            {
                int first = rng.Next(s.Frames), count = 1 + rng.Next(s.Frames - first);
                if (t == 0)
                {
                    first = 0;
                    count = s.Frames;
                }
                if (t == 1)
                {
                    first = Math.Min(1, s.Frames - 1);
                    count = s.Frames - first;
                }
                string from = t % 2 == 1 ? "smine.flb" : "sorig.flb";
                string trimmed = $"trimming frames {first}+{count} of the {(t % 2 == 1 ? "compacted" : "recorded")} screens, {what}";
                File.Delete("smine_trim.flb");
                File.Delete("stheirs_trim.flb");
                mine = Mine(() => FlbCompactor.Trim(from, "smine_trim.flb", first, count));
                if (mine != null)
                {
                    Fail($"{trimmed}: {mine}");
                    continue;
                }
                trims++;

                // The entries it should have: from the last one, up to the first
                // frame's, with the top screen whole, or with the bottom screen
                // whole if that's earlier (only read, up to the first frame's);
                // then each entry with the copies in the range, and at most
                // one fewer missed frames.
                int e0 = shotOf[first], e1 = shotOf[first + count - 1];
                bool TopWhole(int e) => s.Shots[e].Top != null && (s.Shots[e].Flags & FlbClip.TopDelta) == 0;
                bool BottomWhole(int e) => s.Shots[e].Bottom != null && (s.Shots[e].Flags & FlbClip.BottomDelta) == 0;
                int kt = Enumerable.Range(0, e0 + 1).Last(TopWhole);
                int kb = Enumerable.Range(0, e0 + 1).Where(BottomWhole).DefaultIfEmpty(kt).Last();
                int start = Math.Min(kt, kb), frame = 0;
                bool topKnown = false, bottomKnown = false;
                List<Shot> want = new List<Shot>();
                for (int e = 0; e <= e1; e++)
                {
                    Shot shot = s.Shots[e];
                    int copies = Math.Max(0, Math.Min(frame + shot.Copies, first + count) - Math.Max(frame, first));
                    frame += shot.Copies;
                    if (e < start)
                        continue;
                    // (Before its first whole frame in the trimmed clip, a screen is black.)
                    topKnown |= TopWhole(e);
                    bottomKnown |= BottomWhole(e);
                    want.Add(new Shot { Top = shot.Top, Bottom = shot.Bottom, Flags = shot.Flags, Copies = copies,
                                        Missed = copies == 0 ? 0 : Math.Min(shot.Missed, copies - 1),
                                        TopNow = topKnown ? shot.TopNow : null, BottomNow = bottomKnown ? shot.BottomNow : null });
                }
                leads += want.Count(shot => shot.Copies == 0);
                ScreenSpec ts = new ScreenSpec { Width = s.Width, Height = s.Height, BottomWidth = s.BottomWidth, BottomHeight = s.BottomHeight,
                                                 Audio = s.Audio, Inputs = s.Inputs, Codes = s.Codes, Compacted = true,
                                                 Copies = want.Select(shot => shot.Copies).ToArray() };
                CheckScreens(trimmed, "smine_trim.flb", ts, want, s.Inputs, rng, first, false);
                CheckPacked(trimmed, "smine_trim.flb");
                // Its sound: the frames' own, as the clip it was made from gives it.
                if (s.Audio)
                {
                    using (FlbClip a = FlbClip.Open(from))
                        a.WriteWav("swant.wav", first, count);
                    using (FlbClip a = FlbClip.Open("smine_trim.flb"))
                        a.WriteWav("sgot.wav");
                    if (!File.ReadAllBytes("swant.wav").SequenceEqual(File.ReadAllBytes("sgot.wav")))
                        Fail($"{trimmed}: its sound isn't the frames' own");
                }
                if (theirs)
                {
                    string them = Ref(compactRef, $"trim {from} stheirs_trim.flb {first} {count}");
                    if (them != null)
                        Fail($"{trimmed}: the 3DS app: {them}");
                    else
                    {
                        SameBytes(trimmed, "smine_trim.flb", "stheirs_trim.flb");
                        compared++;
                    }
                }
            }
        }
        Console.WriteLine($"  with screens: {trims} trims ({leads} entries kept only to be read), {compared} clips the same as the 3DS app's");
        if (leads == 0)
            Fail("trimming clips with screens: no entry was kept only to be read");
        string why = Mine(() => FlbCompactor.Compact("smine.flb"));
        if (why != "this clip is already compacted")
            Fail($"compacting a compacted clip with screens: \"{why}\"");
        if (Directory.GetFiles(".", "*.tmp").Length != 0)
            Fail("compacting or trimming clips with screens left a .tmp behind");
    }

    // ---- Exporting them

    // Both of a clip's screens as an export shows them (see
    // Converter.PictureSize): the top one, and the bottom one under it, centred.
    static ushort StackedPixel(ScreenSpec s, Shot shot, int x, int y)
    {
        if (y < s.Height)
            return shot.TopNow[y * s.Width + x];
        int x0 = (s.Width - s.BottomWidth) / 2;
        return shot.BottomNow != null && x >= x0 && x < x0 + s.BottomWidth ? shot.BottomNow[(y - s.Height) * s.BottomWidth + x - x0] : (ushort)0;
    }

    // A picture (w x h, BGR) scaled to dw x dh by ffmpeg, with its scale
    // filter's 'flags', as the export's scaler does; and then, as its encoder
    // does on its own, with its colour at half the size each way (H.264's
    // 4:2:0). (In two steps, unlike FfmpegScale: both screens at 1440 pixels
    // tall are 3 times the size, so with nearest, a pixel's colour doesn't
    // fall on whole colour samples, and it matters how those are made.)
    static byte[] ScaledAsExported(string ffmpeg, byte[] picture, int w, int h, int dw, int dh, string flags)
    {
        File.WriteAllBytes("picture.raw", picture);
        return Run(ffmpeg, $"-v error -f rawvideo -pixel_format bgr24 -video_size {w}x{h} -i picture.raw " +
                           $"-vf scale={dw}:{dh}:flags={flags},format=bgr24,format=yuv420p -f rawvideo -pix_fmt bgr24 -").Out;
    }

    // Exports a clip with screens, with its bottom screen under the top one or
    // without, and checks the MP4's size, frames and sound.
    static void CheckScreensConversion(string what, string ffmpeg, string clip, ScreenSpec s, bool bottom, int first = 0, int frames = 0,
                                       ExportSize videoSize = ExportSize.Double, ExportScaling scaling = ExportScaling.Nearest,
                                       ExportEncoder encoder = ExportEncoder.Cpu, double minPsnr = 30)
    {
        checks++;
        string mp4 = Path.ChangeExtension(clip, ".mp4");
        if (frames == 0)
            frames = s.Frames;
        int progress = 0, w, h;
        try
        {
            using (FlbClip c = FlbClip.Open(clip))
            {
                (w, h) = Converter.VideoSize(c, bottom, videoSize);
                Converter.ToMp4(c, first, frames, mp4, InputLayout.Default, bottom, videoSize, scaling, encoder, ffmpeg,
                                done => progress = done, CancellationToken.None);
            }
        }
        catch (Exception x)
        {
            Fail($"{what}: {x.Message}");
            return;
        }
        if (progress != frames * Converter.Passes(videoSize))
            Fail($"{what}: the progress ended at {progress} frames, not {frames * Converter.Passes(videoSize)}");
        bool both = bottom && s.HasBottom;
        int pw = s.Width, ph = both ? s.Height + s.BottomHeight : s.Height;
        (int wantW, int wantH) = Converter.VideoSize(pw, ph, videoSize);
        if (w != wantW || h != wantH)
            Fail($"{what}: a {w}x{h} video, not {wantW}x{wantH}");
        int size = w * h * 3;
        byte[] video = Run(ffmpeg, $"-v error -i \"{mp4}\" -f rawvideo -pix_fmt bgr24 -").Out;
        if (video.Length != frames * size)
        {
            Fail($"{what}: {video.Length / size} frames of {w}x{h}, want {frames}");
            return;
        }
        // Each frame must be its entry's picture (both screens, or the top one),
        // with the input display on the top screen as big as on a video of the
        // top screen alone: like it, and less like the other entries' pictures.
        int[] shotOf = s.ShotOfFrame;
        int topH = both ? h * s.Height / ph : h;
        InputOverlay overlay = s.Inputs ? new InputOverlay(w, topH, InputLayout.Default) : null;
        Dictionary<int, byte[]> pictures = new Dictionary<int, byte[]>();
        byte[] Picture(int e)
        {
            if (!pictures.TryGetValue(e, out byte[] picture))
            {
                Shot shot = s.Shots[e];
                Func<int, int, ushort> pixel = (x, y) => both ? StackedPixel(s, shot, x, y) : shot.TopNow[y * s.Width + x];
                picture = (videoSize == ExportSize.Double || videoSize == ExportSize.Discord) && scaling == ExportScaling.Nearest
                    ? Bgr((x, y) => pixel(x / 2, y / 2), w, h)
                    : ScaledAsExported(ffmpeg, Bgr(pixel, pw, ph), pw, ph, w, h, Converter.ScaleFlags[(int)scaling]);
                pictures[e] = picture;
            }
            return picture;
        }
        double worst = 99;
        for (int v = 0; v < frames; v++)
        {
            int f = first + v;
            byte[] expected = (byte[])Picture(shotOf[f]).Clone();
            overlay?.Draw(expected, InputOf(f));
            double right = Psnr(video, v * size, expected, size);
            // (Another entry's picture: one that differs on the screens shown.)
            double other = 0;
            foreach (int e in shotOf.Distinct().Where(e => e != shotOf[f] && (s.Shots[e].TopNow != s.Shots[shotOf[f]].TopNow ||
                                                                              (both && s.Shots[e].BottomNow != s.Shots[shotOf[f]].BottomNow))))
            {
                byte[] wrong = (byte[])Picture(e).Clone();
                overlay?.Draw(wrong, InputOf(f));
                other = Math.Max(other, Psnr(video, v * size, wrong, size));
            }
            worst = Math.Min(worst, right);
            if (right < minPsnr || right < other + 6)
            {
                Fail($"{what}: frame {f} is {right:F1} dB from what it should be ({other:F1} from another)");
                return;
            }
        }
        Console.WriteLine($"  {what}: {frames} frames of {w}x{h}, each within {worst:F1} dB (PSNR) of the clip's");

        var audio = Run(ffmpeg, $"-v error -i \"{mp4}\" -map 0:a -f s16le -ac 2 -ar 48000 -");
        long samples = audio.Out.Length / 4, want = (long)frames * 48000 / Fps;
        if (s.Audio ? audio.Exit != 0 || Math.Abs(samples - want) > 1024 : audio.Exit == 0)
            Fail($"{what}: {samples} sound samples, want {(s.Audio ? "about " + want : "none")}");
    }

    static void TestScreensConversions(string ffmpeg)
    {
        Random rng = new Random(5);
        // (Pictures that differ a lot from one to the next, so each frame can be told from the others.)
        ScreenSpec s = new ScreenSpec { Width = 400, Height = 240, BottomWidth = 320, BottomHeight = 240, Audio = true, Inputs = true, Codes = true,
                                        Key = 4, Kinds = new[] { 0, 6, 3, 2 } };
        do
            Build(s, rng, 14, 2);
        while (s.Shots.Count < 7 || s.Shots.Count(shot => shot.Bottom != null && shot.Copies != 0) < 3 ||
               !s.Shots.Any(shot => shot.Copies == 0) || !s.Shots.Any(shot => (shot.Flags & FlbClip.TopDelta) != 0) ||
               !s.Shots.Any(shot => shot.Bottom != null && shot.Copies != 0 && shot.Bottom.Length != 320 * 240 * 2));
        WriteScreens("both.flb", s);
        CheckScreens("screens, for the exports", "both.flb", s, s.Shots, true, rng);
        CheckScreensConversion("both screens, 800x960", ffmpeg, "both.flb", s, true);
        CheckScreensConversion("both screens' clip, the top one alone", ffmpeg, "both.flb", s, false);
        CheckScreensConversion("both screens, a trimmed part", ffmpeg, "both.flb", s, true, 2, s.Frames - 3);
        CheckScreensConversion("both screens, 900x1080, bicubic", ffmpeg, "both.flb", s, true, videoSize: ExportSize.P1080, scaling: ExportScaling.Bicubic);
        CheckScreensConversion("both screens, 1200x1440, nearest, a trimmed part", ffmpeg, "both.flb", s, true, 1, s.Frames - 2, ExportSize.P1440);
        CheckScreensConversion("both screens, 900x1080, nearest", ffmpeg, "both.flb", s, true, videoSize: ExportSize.P1080);
        CheckScreensConversion("both screens, for Discord", ffmpeg, "both.flb", s, true, videoSize: ExportSize.Discord);
        CheckScreensConversion("both screens' clip, the top one alone, 1800x1080, lanczos", ffmpeg, "both.flb", s, false, videoSize: ExportSize.P1080,
                               scaling: ExportScaling.Lanczos);
        if (Converter.GpuAvailable(ffmpeg))
            CheckScreensConversion("both screens, by the GPU, 1800x2160, bilinear", ffmpeg, "both.flb", s, true, videoSize: ExportSize.P2160,
                                   scaling: ExportScaling.Bilinear, encoder: ExportEncoder.Gpu);

        // Half resolution, compacted, without sound or inputs: 400x480.
        ScreenSpec half = new ScreenSpec { Width = 200, Height = 120, BottomWidth = 160, BottomHeight = 120, Compacted = true, Key = 3, Kinds = new[] { 0, 6, 3 } };
        do
            Build(half, rng, 10);
        while (half.Shots.Count < 5);
        WriteScreens("half.flb", half);
        CheckScreensConversion("both screens at half resolution, compacted, 400x480", ffmpeg, "half.flb", half, true);
        // A clip with screens but no bottom one: asked for or not, the top screen's video.
        ScreenSpec alone = new ScreenSpec { Width = 400, Height = 240, BottomWidth = 0, BottomHeight = 0, Audio = true, Inputs = true, Kinds = new[] { 0, 6, 3, 2 } };
        do
            Build(alone, rng, 10);
        while (alone.Shots.Count < 5);
        WriteScreens("alone.flb", alone);
        CheckScreensConversion("a clip with screens, the top one only", ffmpeg, "alone.flb", alone, true);
        byte[] asked = File.ReadAllBytes("alone.mp4");
        CheckScreensConversion("a clip with screens, the top one only, the bottom one not asked for", ffmpeg, "alone.flb", alone, false);
        if (!asked.SequenceEqual(File.ReadAllBytes("alone.mp4")))
            Fail("a clip without a bottom screen exports differently when one is asked for");

        // A frame that turns out damaged part way: nothing left behind.
        string[] before = Leftovers(".");
        long index = WriteScreens("sdamaged.flb", s);
        int bad = s.Shots.FindLastIndex(shot => shot.Bottom != null && shot.Copies != 0 && shot.Bottom.Length != 320 * 240 * 2);
        Patch32("sdamaged.flb", index + bad * 24 + 8, s.Shots[bad].Bottom.Length - 1);
        foreach (ExportScaling scaling in new[] { ExportScaling.Nearest, ExportScaling.Spline })
        {
            checks++;
            try
            {
                using (FlbClip c = FlbClip.Open("sdamaged.flb"))
                    Converter.ToMp4(c, 0, c.FrameCount, "sdamaged.mp4", InputLayout.Default, true, ExportSize.Double, scaling, ExportEncoder.Cpu, ffmpeg,
                                    _ => { }, CancellationToken.None);
                Fail($"both screens, damaged part way ({scaling}): finished");
            }
            catch (Exception x)
            {
                if (x.Message != $"frame {bad} is damaged")
                    Fail($"both screens, damaged part way ({scaling}): {x.GetType().Name}: {x.Message}");
            }
            if (File.Exists("sdamaged.mp4") || Leftovers(".").Length != before.Length)
                Fail($"both screens, damaged part way ({scaling}): left {string.Join(", ", Leftovers(".").Except(before))}");
        }
    }
}
