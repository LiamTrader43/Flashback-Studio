// PC test of Flashback Studio for Windows (windows-app/), with the app's own code:
//  - its clip reader, on clips of every version: as the recorder saves them
//    (the ring of frames and the sound wrapping around) and as Flashback Studio
//    compacts them, with and without sound and inputs, with copies of missed
//    frames, and some frames compressed. Every frame, input and sound sample
//    must read back as written. Clips from older builds (.raw and .txt) too,
//    and damaged clips, which must fail with the right message (or, with
//    damaged inputs, read without them);
//  - its compacting and trimming, against the 3DS app's (compact_ref, built
//    with 3ds-app/source/flb.c): they must write the same bytes;
//  - its input display, against the 3DS app's own (overlay_ref, built from
//    3ds-app/source/main.c), for many inputs;
//  - whole exports with ffmpeg, if it's found (on PATH): the frames, the input
//    display and the sound in the MP4, a trimmed part, replacing an MP4,
//    stopping, and a clip that turns out damaged part way; encoded by the GPU
//    too, if this PC has an NVIDIA card.
//
// usage: WindowsAppTest <overlay_ref.exe> <compact_ref.exe>
// It writes its clips in the current folder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FlashbackStudio;

static class WindowsAppTest
{
    const int Fps = 30;
    const long RateMhz = 32728498;
    const int SlotPad = 32;     // (recorded) a ring slot's room past an uncompressed frame
    static int errors, checks;

    static void Fail(string message)
    {
        errors++;
        if (errors < 30)
            Console.WriteLine("  ERROR: " + message);
    }

    // Entry e's picture: runs of 4 pixels, so the frames compress.
    static ushort PixelOf(int e, int x, int y) => (ushort)(e * 977 + y * 263 + x / 4 * 31 + 5);

    // Frame f's inputs, as the recorder would have recorded them (as in flb_test.c).
    static FlbInput InputOf(int f) => new FlbInput
    {
        Buttons = (uint)f * 2654435761u >> 3 | FlbInput.KeyTouch,
        CircleX = (short)(f * 3 % 313 - 156),
        CircleY = (short)(156 - f * 7 % 313),
        TouchX = (ushort)(f % 320),
        TouchY = (ushort)(f % 240),
        CStickX = (short)(f & 0xFF),
        CStickY = (short)-(f & 0x7F),
    };

    // Sound sample i, left and right.
    static short Left(long i) => (short)(i * 7);
    static short Right(long i) => (short)~(i * 7);

    // ---- Writing clips

    sealed class Spec
    {
        public int Width = 8, Height = 6;
        public int[] Copies;            // each entry's
        public bool Audio, Inputs, Compacted;
        public int Frames => Copies.Sum();
        public int Version => Audio ? 3 : Compacted ? 2 : 1;
        // (with sound) the samples the clip holds, where its track starts, and the ring's size
        public long First, End, TrackStart, Capacity;
        public long TrackLength => (Frames * RateMhz / Fps + 500) / 1000;
    }

    // The recorder's compression, with only its "pixel" and "run" ops.
    static byte[] Compress(ushort[] pixels)
    {
        List<byte> o = new List<byte>();
        ushort px = 0;
        for (int i = 0; i < pixels.Length; )
        {
            if (pixels[i] != px)
            {
                px = pixels[i++];
                o.Add(0xFE);
                o.Add((byte)px);
                o.Add((byte)(px >> 8));
                continue;
            }
            int run = 0;
            while (i < pixels.Length && pixels[i] == px && run < 62)
            {
                i++;
                run++;
            }
            o.Add((byte)(0xC0 | (run - 1)));
        }
        return o.ToArray();
    }

    // Entry e as stored: every other one compressed.
    static byte[] Stored(Spec s, int e)
    {
        ushort[] px = new ushort[s.Width * s.Height];
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                px[y * s.Width + x] = PixelOf(e, x, y);
        byte[] packed = Compress(px);
        if (e % 2 == 1 && packed.Length < px.Length * 2)
            return packed;
        byte[] raw = new byte[px.Length * 2];
        Buffer.BlockCopy(px, 0, raw, 0, raw.Length);
        return raw;
    }

    static void Put32(Stream f, long v) => f.Write(BitConverter.GetBytes((uint)v), 0, 4);
    static void Put16(Stream f, int v) => f.Write(BitConverter.GetBytes((ushort)v), 0, 2);

    // Writes a clip as 'spec' says; returns where its index starts.
    static long Write(string path, Spec s)
    {
        int entries = s.Copies.Length, slotBytes = s.Width * s.Height * 2, stride = slotBytes + SlotPad;
        int slots = entries + 2, firstSlot = entries / 2;      // (recorded: the ring wraps)
        byte[][] stored = Enumerable.Range(0, entries).Select(e => Stored(s, e)).ToArray();
        if (s.Audio)
        {
            if (s.Compacted)
            {
                // as Studio compacts: just the track, all of it held
                s.Capacity = s.TrackLength;
                s.TrackStart = s.First = 5000;
                s.End = s.First + s.TrackLength;
            }
            else
            {
                // as recorded: a ring of sound that has wrapped around a few times, starting a little
                // after the first frame (recording started) and ending a little before the last
                s.Capacity = s.TrackLength + 50;
                s.First = 3 * s.Capacity + 17;
                s.TrackStart = s.First - 20;
                s.End = s.TrackStart + s.TrackLength - 30;
            }
        }

        using (FileStream f = new FileStream(path, FileMode.Create))
        {
            long audioOffset = 0, videoOffset = 0;
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

            if (s.Audio && !s.Compacted)
            {
                WriteAudio();
                f.Position = (f.Position + 511) / 512 * 512;
            }
            videoOffset = f.Position;
            if (s.Compacted)
                for (int e = 0; e < entries; e++)
                {
                    offsets[e] = f.Position;
                    f.Write(stored[e], 0, stored[e].Length);
                }
            else
            {
                f.SetLength(videoOffset + (long)slots * stride);
                for (int e = 0; e < entries; e++)
                {
                    offsets[e] = videoOffset + (long)(firstSlot + e) % slots * stride;
                    f.Position = offsets[e];
                    f.Write(stored[e], 0, stored[e].Length);
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
                Put32(f, s.Version == 1 ? (firstSlot + e) % slots : offsets[e]);
                Put32(f, stored[e].Length);
                Put32(f, s.Copies[e]);
                if (s.Audio)
                    Put32(f, s.TrackStart + frame * RateMhz / (Fps * 1000));
                frame += s.Copies[e];
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
            foreach (long v in new long[] { 0x4B424C46, s.Version, s.Width, s.Height, Fps, s.Compacted ? 0 : stride,
                                            slotBytes, entries, s.Frames, indexOffset, inputOffset, 0 })
                Put32(f, v);
            return indexOffset;
        }
    }

    // Changes 4 bytes at 'offset' (from the end if negative) of a file.
    static void Patch32(string path, long offset, long v)
    {
        using (FileStream f = new FileStream(path, FileMode.Open))
        {
            f.Position = offset < 0 ? f.Length + offset : offset;
            Put32(f, v);
        }
    }

    // ---- Reading them back

    static void CheckClip(string what, string path, Spec s, bool inputs)
    {
        checks++;
        try
        {
            using (FlbClip clip = FlbClip.Open(path))
            {
                if (clip.Width != s.Width || clip.Height != s.Height || clip.Fps != Fps || clip.FrameCount != s.Frames)
                    Fail($"{what}: {clip.Width}x{clip.Height}, {clip.Fps} fps, {clip.FrameCount} frames");
                if (clip.HasAudio != s.Audio)
                    Fail($"{what}: sound {(s.Audio ? "missing" : "where there was none")}");
                if ((clip.Inputs != null) != inputs)
                    Fail($"{what}: inputs {(inputs ? "missing" : "where there were none")}");
                else if (inputs && !Enumerable.Range(0, s.Frames).All(i => clip.Inputs[i].Equals(InputOf(i))))
                    Fail($"{what}: the wrong inputs");

                int e = 0;
                foreach ((ushort[] px, int copies) in clip.Frames())
                {
                    if (e >= s.Copies.Length || copies != s.Copies[e])
                    {
                        Fail($"{what}: entry {e} shown {copies} times");
                        break;
                    }
                    for (int i = 0; i < px.Length; i++)
                        if (px[i] != PixelOf(e, i % s.Width, i / s.Width))
                        {
                            Fail($"{what}: entry {e} shows the wrong picture");
                            break;
                        }
                    e++;
                }
                if (e != s.Copies.Length)
                    Fail($"{what}: {e} entries read");

                if (s.Audio)
                {
                    string wav = path + ".wav";
                    clip.WriteWav(wav);
                    CheckWav(what, wav, s, 0, s.Frames);
                    // (and a part of it, as a trimmed part is exported)
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

    // The sound of frames first .. first + frames - 1: from the sample the first
    // of them is shown at, as many samples as they last.
    static void CheckWav(string what, string wav, Spec s, int first, int frames)
    {
        byte[] b = File.ReadAllBytes(wav);
        long count = ((long)frames * RateMhz / Fps + 500) / 1000;
        int e = EntryOfFrame(s)[first], entryFrame = s.Copies.Take(e).Sum();
        long start = s.TrackStart + entryFrame * RateMhz / (Fps * 1000) + (first - entryFrame) * RateMhz / (Fps * 1000);
        if (b.Length != 44 + count * 4 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" ||
            BitConverter.ToUInt32(b, 24) != (RateMhz + 500) / 1000 || BitConverter.ToUInt16(b, 22) != 2 ||
            BitConverter.ToUInt32(b, 40) != count * 4)
        {
            Fail($"{what}: the WAV's header is wrong ({b.Length} bytes)");
            return;
        }
        for (long k = 0; k < count; k++)
        {
            long i = start + k;
            bool held = i >= s.First && i < s.End;
            short l = BitConverter.ToInt16(b, (int)(44 + k * 4)), r = BitConverter.ToInt16(b, (int)(46 + k * 4));
            if (l != (held ? Left(i) : 0) || r != (held ? Right(i) : 0))
            {
                Fail($"{what}: sound sample {k} is {l}, {r}");
                return;
            }
        }
    }

    static void CheckFails(string what, string path, string message)
    {
        checks++;
        try
        {
            using (FlbClip clip = FlbClip.Open(path))
                foreach ((ushort[] px, int copies) in clip.Frames())
                {
                }
            Fail($"{what}: read without complaint");
        }
        catch (InvalidDataException x)
        {
            if (x.Message != message)
                Fail($"{what}: \"{x.Message}\", want \"{message}\"");
        }
    }

    static void TestReader()
    {
        Random rng = new Random(99);
        for (int run = 0; run < 48; run++)
        {
            Spec s = new Spec
            {
                Audio = run % 3 != 0,
                Inputs = run % 4 != 1,
                Compacted = run % 2 == 1,
                Copies = Enumerable.Range(0, 1 + rng.Next(40)).Select(_ => rng.Next(4) == 0 ? 2 + rng.Next(3) : 1).ToArray(),
            };
            if (run % 5 == 4)
            {
                s.Width = 400;
                s.Height = 240;
            }
            Write("clip.flb", s);
            CheckClip($"version {s.Version}{(s.Compacted ? " compacted" : "")}, {s.Width}x{s.Height}, {s.Frames} frames" +
                      $"{(s.Audio ? ", sound" : "")}{(s.Inputs ? ", inputs" : "")}", "clip.flb", s, s.Inputs);
        }

        // Damaged clips
        Spec d = new Spec { Audio = true, Inputs = true, Copies = new[] { 1, 3, 1, 2 } };
        long index = Write("good.flb", d);
        void Damaged(string what, Action<string> damage, string message)
        {
            File.Copy("good.flb", "bad.flb", true);
            damage("bad.flb");
            CheckFails(what, "bad.flb", message);
        }
        Damaged("cut off", p => { using (FileStream f = new FileStream(p, FileMode.Open)) f.SetLength(f.Length - 100); },
                "not a Flashback clip (or the copy is incomplete)");
        Damaged("tiny", p => File.WriteAllBytes(p, new byte[20]), "not a Flashback clip (file too small)");
        Damaged("from a newer version", p => Patch32(p, -44, 4),
                "made by a newer Flashback version; get the latest Flashback Studio");
        Damaged("too wide", p => Patch32(p, -40, 401), "the clip's header is damaged");
        Damaged("index in the wrong place", p => Patch32(p, -12, index + 4), "the clip's header is damaged");
        Damaged("mono sound", p => Patch32(p, -48 - 32 + 4, 1 | 16 << 16), "the clip's audio header is damaged");
        Damaged("an entry shown 0 times", p => Patch32(p, index + 16 + 8, 0), "frame entry 1 is damaged");
        Damaged("frames miscounted", p => Patch32(p, -16, d.Frames + 1), "the clip's frame count doesn't match its index");
        Damaged("a frame's data cut short", p => Patch32(p, index + 16 + 4, Stored(d, 1).Length - 1), "frame 1 is damaged");

        // Damaged inputs: the clip still reads, without them
        long inputs = index - 16 - d.Frames * 16;
        foreach ((string what, long offset, long value) in new[] {
            ("inputs' magic wrong", inputs, 0x12345678L),
            ("inputs' count wrong", inputs + 8, 4L),
            ("inputs' record size too small", inputs + 12, 8L),
            ("inputs too near the index", -8L, index - 8) })
        {
            File.Copy("good.flb", "bad.flb", true);
            Patch32("bad.flb", offset, value);
            CheckClip(what, "bad.flb", d, false);
        }

        // Clips from older builds: raw frames, and a .txt
        Spec raw = new Spec { Copies = Enumerable.Repeat(1, 7).ToArray() };
        using (FileStream f = new FileStream("old.raw", FileMode.Create))
            for (int e = 0; e < 7; e++)
                for (int y = 0; y < raw.Height; y++)
                    for (int x = 0; x < raw.Width; x++)
                        Put16(f, PixelOf(e, x, y));
        File.WriteAllText("old.txt", $"Flashback clip\r\nResolution: {raw.Width}x{raw.Height}\r\nFrame rate: {Fps}\r\n");
        CheckClip("an older build's .raw", "old.raw", raw, false);
        File.WriteAllText("old.txt", "Resolution: unknown\r\n");
        CheckFails("a .raw with a damaged .txt", "old.raw", "couldn't read the resolution or frame rate from its .txt");
        File.Delete("old.txt");
        CheckFails("a .raw without its .txt", "old.raw", "its .txt is missing, so its resolution and frame rate are unknown");
    }

    // ---- Compacting and trimming, against the 3DS app's

    // Runs compact_ref; null, or why it failed.
    static string Ref(string compactRef, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(compactRef, args) { UseShellExecute = false, RedirectStandardOutput = true };
        using (Process p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? null : output;
        }
    }

    // Compacts or trims with the app's FlbCompactor; null, or why it failed.
    static string Mine(Func<FlbCompactor> begin)
    {
        try
        {
            FlbCompactor c = begin();
            while (!c.Step())
            {
            }
            return null;
        }
        catch (InvalidDataException x)
        {
            return x.Message;
        }
    }

    static void SameBytes(string what, string mine, string theirs)
    {
        checks++;
        byte[] a = File.ReadAllBytes(mine), b = File.ReadAllBytes(theirs);
        if (!a.SequenceEqual(b))
            Fail($"{what}: {a.Length} bytes, and the 3DS app's {b.Length}, differ");
    }

    static void TestCompactor(string compactRef)
    {
        Random rng = new Random(7);
        for (int run = 0; run < 30; run++)
        {
            Spec s = new Spec
            {
                Audio = run % 3 != 0,
                Inputs = run % 4 != 1,
                Copies = Enumerable.Range(0, 1 + rng.Next(30)).Select(_ => rng.Next(4) == 0 ? 2 + rng.Next(3) : 1).ToArray(),
            };
            if (run % 6 == 5)
            {
                s.Width = 400;
                s.Height = 240;
            }
            string what = $"version {s.Version}, {s.Width}x{s.Height}, {s.Frames} frames{(s.Audio ? ", sound" : "")}{(s.Inputs ? ", inputs" : "")}";
            Write("orig.flb", s);
            File.Copy("orig.flb", "mine.flb", true);
            File.Copy("orig.flb", "theirs.flb", true);
            string mine = Mine(() => FlbCompactor.Compact("mine.flb")), theirs = Ref(compactRef, "compact theirs.flb");
            if (mine != null || theirs != null)
                Fail($"compacting {what}: {mine ?? "ok"} / the 3DS app: {theirs ?? "ok"}");
            else
                SameBytes("compacting " + what, "mine.flb", "theirs.flb");

            // Trimmed, from the recorded clip and from the compacted one: the
            // start, the end, and at random (sometimes part way through a
            // frame's copies).
            for (int t = 0; t < 6; t++)
            {
                int first = rng.Next(s.Frames), count = 1 + rng.Next(s.Frames - first);
                if (t == 0)
                {
                    first = 0;
                    count = Math.Max(1, s.Frames - 1);
                }
                if (t == 1)
                {
                    first = Math.Min(1, s.Frames - 1);
                    count = s.Frames - first;
                }
                string from = t % 2 == 1 ? "mine.flb" : "orig.flb";
                File.Delete("mine_trim.flb");
                File.Delete("theirs_trim.flb");
                mine = Mine(() => FlbCompactor.Trim(from, "mine_trim.flb", first, count));
                theirs = Ref(compactRef, $"trim {from} theirs_trim.flb {first} {count}");
                string trimmed = $"trimming frames {first}+{count} of the {(t % 2 == 1 ? "compacted" : "recorded")} {what}";
                if (mine != null || theirs != null)
                    Fail($"{trimmed}: {mine ?? "ok"} / the 3DS app: {theirs ?? "ok"}");
                else
                    SameBytes(trimmed, "mine_trim.flb", "theirs_trim.flb");
            }
        }

        // What's refused, with the 3DS app's messages, and nothing left behind.
        void Refused(string what, Func<FlbCompactor> begin, string message)
        {
            checks++;
            string why = Mine(begin);
            if (why != message)
                Fail($"{what}: \"{why}\", want \"{message}\"");
        }
        Refused("compacting a compacted clip", () => FlbCompactor.Compact("mine.flb"), "this clip is already compacted");
        Refused("trimming onto another clip", () => FlbCompactor.Trim("orig.flb", "mine.flb", 0, 1), "there's already a clip with that name");
        Refused("trimming frames that aren't there", () => FlbCompactor.Trim("orig.flb", "new.flb", 0, 100000), "those frames aren't in the clip");
        if (Directory.GetFiles(".", "*.tmp").Length != 0)
            Fail("compacting or trimming left a .tmp behind");
    }

    // ---- The input display

    // (overlay_ref.c draws on the same)
    static ushort Background(int x, int y) => (ushort)((x * 31 / 399) << 11 | (y * 63 / 239) << 5 | ((x ^ y) & 31));

    static byte[] Bgr(Func<int, int, ushort> pixel, int w, int h)
    {
        byte[] f = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                ushort p = pixel(x, y);
                int o = (y * w + x) * 3, r = p >> 11, g = (p >> 5) & 63, b = p & 31;
                f[o] = (byte)(b << 3 | b >> 2);
                f[o + 1] = (byte)(g << 2 | g >> 4);
                f[o + 2] = (byte)(r << 3 | r >> 2);
            }
        return f;
    }

    static void TestOverlay(string overlayRef)
    {
        uint[] keys = { 1u << 9, 1u << 8, 1u << 6, 1u << 7, 1u << 5, 1u << 4, 1u << 10, 1u << 11, 1, 2, 4, 8 };
        List<FlbInput> cases = new List<FlbInput> { new FlbInput() };
        foreach (uint k in keys)
            cases.Add(new FlbInput { Buttons = k, CircleX = (short)(k % 313 - 156), CircleY = (short)(k * 7 % 313 - 156) });
        cases.Add(new FlbInput { Buttons = keys.Aggregate((a, b) => a | b) | FlbInput.KeyTouch, CircleX = 156, CircleY = 156 });
        cases.Add(new FlbInput { Buttons = 1u << 6 | 1u << 5 });      // (two of the D-Pad's arms)
        cases.Add(new FlbInput { CircleX = 400, CircleY = -400 });      // (past where the knob reaches)
        cases.Add(new FlbInput { CircleX = -156 });
        foreach ((int x, int y) in new[] { (0, 0), (319, 239), (160, 120), (500, 300), (37, 201) })
            cases.Add(new FlbInput { Buttons = FlbInput.KeyTouch, TouchX = (ushort)x, TouchY = (ushort)y });
        cases.Add(new FlbInput { TouchX = 100, TouchY = 100 });         // (a touch position without KEY_TOUCH: not shown)

        using (FileStream f = new FileStream("inputs.bin", FileMode.Create))
            foreach (FlbInput n in cases)
            {
                Put32(f, n.Buttons);
                Put16(f, n.CircleX);
                Put16(f, n.CircleY);
                Put16(f, n.TouchX);
                Put16(f, n.TouchY);
                Put16(f, n.CStickX);
                Put16(f, n.CStickY);
            }
        Process p = Process.Start(new ProcessStartInfo(overlayRef, "inputs.bin studio.bin") { UseShellExecute = false });
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Fail("overlay_ref failed");
            return;
        }
        byte[] studio = File.ReadAllBytes("studio.bin");
        if (studio.Length != cases.Count * 400 * 240 * 2)
        {
            Fail($"overlay_ref wrote {studio.Length} bytes");
            return;
        }

        // The same size as on the 3DS: Studio's picture, but for the rounding of
        // its 16-bit colours.
        InputOverlay overlay = new InputOverlay(400, 240);
        for (int c = 0; c < cases.Count; c++)
        {
            checks++;
            byte[] mine = Bgr(Background, 400, 240);
            overlay.Draw(mine, cases[c]);
            byte[] theirs = Bgr((x, y) => BitConverter.ToUInt16(studio, (c * 400 * 240 + y * 400 + x) * 2), 400, 240);
            int worst = 0;
            long sum = 0;
            for (int i = 0; i < mine.Length; i++)
            {
                int diff = Math.Abs(mine[i] - theirs[i]);
                worst = Math.Max(worst, diff);
                sum += diff;
            }
            if (worst > 20 || sum > 35000)      // (they differ by up to about 12, and 27000 in all)
                Fail($"input display case {c}: differs from Studio's by up to {worst} (in all {sum})");
        }
        Console.WriteLine($"{cases.Count} input display cases compared with Studio's");
    }

    // ---- Converting

    static (byte[] Out, string Err, int Exit) Run(string exe, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (Process p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            MemoryStream o = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(o);
            p.WaitForExit();
            return (o.ToArray(), err.Result, p.ExitCode);
        }
    }

    static double Psnr(byte[] a, int ao, byte[] b, int len)
    {
        double sum = 0;
        for (int i = 0; i < len; i++)
        {
            double d = a[ao + i] - b[i];
            sum += d * d;
        }
        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * len / sum);
    }

    // Each entry of a clip, copies included.
    static int[] EntryOfFrame(Spec s) => s.Copies.SelectMany((copies, e) => Enumerable.Repeat(e, copies)).ToArray();

    // A picture (w x h, BGR) scaled to dw x dh by ffmpeg, with its scale
    // filter's 'flags', as the export does; then, as the MP4 has it, with its
    // colour at half the size each way (H.264's 4:2:0). (Scaled smoothly, these
    // tests' colourful noise pictures have fine colour that 4:2:0 loses: to
    // about 24 dB at twice the size. Left in, it would hide the encoding's
    // own loss, which is what the frames are compared for.)
    static byte[] FfmpegScale(string ffmpeg, byte[] picture, int w, int h, int dw, int dh, string flags)
    {
        File.WriteAllBytes("picture.raw", picture);
        return Run(ffmpeg, $"-v error -f rawvideo -pixel_format bgr24 -video_size {w}x{h} -i picture.raw " +
                           $"-vf scale={dw}:{dh}:flags={flags},format=yuv420p -f rawvideo -pix_fmt bgr24 -").Out;
    }

    // Converts 'clip' and checks the MP4's frames and sound.
    // (Frames first .. first + frames - 1 of it, if 'frames' isn't 0: a trimmed part.)
    static void CheckConversion(string what, string ffmpeg, string clip, Spec s, bool inputs, int first = 0, int frames = 0,
                                ExportSize videoSize = ExportSize.Double, ExportScaling scaling = ExportScaling.Nearest,
                                ExportEncoder encoder = ExportEncoder.Cpu)
    {
        checks++;
        string mp4 = Path.ChangeExtension(clip, ".mp4");
        if (frames == 0)
            frames = s.Frames;
        try
        {
            using (FlbClip c = FlbClip.Open(clip))
                Converter.ToMp4(c, first, frames, mp4, inputs, videoSize, scaling, encoder, ffmpeg, _ => { }, CancellationToken.None);
        }
        catch (Exception x)
        {
            Fail($"{what}: {x.Message}");
            return;
        }

        (int w, int h) = Converter.VideoSize(s.Width, s.Height, videoSize);
        int size = w * h * 3;
        byte[] video = Run(ffmpeg, $"-v error -i \"{mp4}\" -f rawvideo -pix_fmt bgr24 -").Out;
        if (video.Length != frames * size)
        {
            Fail($"{what}: {video.Length / size} frames, want {frames}");
            return;
        }
        // Each frame must be the right picture (at twice the size, or scaled by
        // ffmpeg as asked, with the input display over it if asked): like it,
        // and less like the frames of the other entries.
        int[] entryOf = EntryOfFrame(s);
        InputOverlay overlay = inputs && s.Inputs ? new InputOverlay(w, h) : null;
        byte[][] pictures = new byte[s.Copies.Length][], expected = new byte[s.Frames][];
        for (int e = 0; e < s.Copies.Length; e++)
            pictures[e] = videoSize == ExportSize.Double && scaling == ExportScaling.Nearest
                ? Bgr((x, y) => PixelOf(e, x / 2, y / 2), w, h)
                : FfmpegScale(ffmpeg, Bgr((x, y) => PixelOf(e, x, y), s.Width, s.Height), s.Width, s.Height, w, h,
                              Converter.ScaleFlags[(int)scaling]);
        for (int f = 0; f < s.Frames; f++)
        {
            expected[f] = (byte[])pictures[entryOf[f]].Clone();
            overlay?.Draw(expected[f], InputOf(f));
        }
        double worst = 99;
        for (int v = 0; v < frames; v++)
        {
            int f = first + v;
            double right = Psnr(video, v * size, expected[f], size);
            double other = Enumerable.Range(0, s.Frames).Where(g => entryOf[g] != entryOf[f])
                                     .Select(g => Psnr(video, v * size, expected[g], size)).DefaultIfEmpty(0).Max();
            worst = Math.Min(worst, right);
            if (right < 30 || right < other + 6)
            {
                Fail($"{what}: frame {f} is {right:F1} dB from what it should be ({other:F1} from another)");
                return;
            }
        }
        Console.WriteLine($"  {what}: {frames} frames, each within {worst:F1} dB (PSNR) of the clip's");

        var audio = Run(ffmpeg, $"-v error -i \"{mp4}\" -map 0:a -f s16le -ac 2 -ar 48000 -");
        if (!s.Audio)
        {
            if (audio.Exit == 0)
                Fail($"{what}: has sound");
            return;
        }
        long samples = audio.Out.Length / 4, want = (long)frames * 48000 / Fps;
        if (audio.Exit != 0 || Math.Abs(samples - want) > 1024)
            Fail($"{what}: {samples} sound samples, want about {want}");
    }

    static string[] Leftovers(string dir) =>
        Directory.GetFiles(dir, "*.part").Concat(Directory.GetFiles(Path.GetTempPath(), "FlashbackStudio-*.wav")).ToArray();

    static void TestConversions(string ffmpeg)
    {
        Spec s = new Spec { Width = 400, Height = 240, Audio = true, Inputs = true, Copies = new[] { 1, 3, 1, 2, 1, 1, 4, 1 } };
        Write("big.flb", s);
        CheckConversion("recorded, with sound and inputs", ffmpeg, "big.flb", s, true);
        CheckConversion("without the input display", ffmpeg, "big.flb", s, false);      // (and replacing the MP4)
        Spec plain = new Spec { Width = 400, Height = 240, Compacted = true, Copies = new[] { 2, 1, 1, 3 } };
        Write("plain.flb", plain);
        CheckConversion("compacted, without sound or inputs", ffmpeg, "plain.flb", plain, true);
        CheckConversion("a trimmed part (frames 2-9)", ffmpeg, "big.flb", s, true, 2, 8);
        // Scaled by ffmpeg, which the frames come back from for the input display.
        CheckConversion("1800x1080, bicubic", ffmpeg, "big.flb", s, true, videoSize: ExportSize.P1080, scaling: ExportScaling.Bicubic);
        CheckConversion("800x480, lanczos, a trimmed part", ffmpeg, "big.flb", s, true, 3, 9, scaling: ExportScaling.Lanczos);
        CheckConversion("2400x1440, nearest, compacted", ffmpeg, "plain.flb", plain, true, videoSize: ExportSize.P1440);
        // Encoded by the GPU, if this PC has one ffmpeg can use (an NVIDIA card).
        if (Converter.GpuAvailable(ffmpeg))
        {
            CheckConversion("by the GPU", ffmpeg, "big.flb", s, true, encoder: ExportEncoder.Gpu);
            CheckConversion("by the GPU, 1800x1080, bicubic, a trimmed part", ffmpeg, "big.flb", s, true, 2, 8, ExportSize.P1080,
                            ExportScaling.Bicubic, ExportEncoder.Gpu);
        }
        else
            Console.WriteLine("  no NVIDIA encoder here: exports by the GPU weren't tested");

        // Stopping, and a clip that turns out damaged part way (scaled here, and
        // by ffmpeg): the MP4 that was there is left as it was, and nothing else
        // is left behind.
        string[] before = Leftovers(".");
        File.WriteAllText("big.mp4", "the old MP4");
        void Stopped(string what, string clip, CancellationToken cancel, string message, ExportScaling scaling = ExportScaling.Nearest)
        {
            checks++;
            try
            {
                using (FlbClip c = FlbClip.Open(clip))
                    Converter.ToMp4(c, "big.mp4", true, ExportSize.Double, scaling, ExportEncoder.Cpu, ffmpeg, _ => { }, cancel);
                Fail($"{what}: finished");
            }
            catch (Exception x)
            {
                if (message != null ? x.Message != message : !(x is OperationCanceledException))
                    Fail($"{what}: {x.GetType().Name}: {x.Message}");
            }
            if (File.ReadAllText("big.mp4") != "the old MP4")
                Fail($"{what}: the MP4 was replaced");
            if (Leftovers(".").Length != before.Length)
                Fail($"{what}: left {string.Join(", ", Leftovers(".").Except(before))}");
        }
        CancellationTokenSource stop = new CancellationTokenSource();
        stop.Cancel();
        Stopped("stopped", "big.flb", stop.Token, null);
        Stopped("stopped, scaled by ffmpeg", "big.flb", stop.Token, null, ExportScaling.Bilinear);
        long index = Write("damaged.flb", s);
        Patch32("damaged.flb", index + 3 * 16 + 4, Stored(s, 3).Length - 1);
        Stopped("damaged part way", "damaged.flb", CancellationToken.None, "frame 3 is damaged");
        Stopped("damaged part way, scaled by ffmpeg", "damaged.flb", CancellationToken.None, "frame 3 is damaged", ExportScaling.Spline);
    }

    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("usage: WindowsAppTest <overlay_ref.exe> <compact_ref.exe>");
            return 2;
        }
        TestReader();
        Console.WriteLine($"{checks} clips checked");
        int compared = checks;
        TestCompactor(Path.GetFullPath(args[1]));
        Console.WriteLine($"{checks - compared} compacted and trimmed clips compared with the 3DS app's");
        TestOverlay(Path.GetFullPath(args[0]));
        string ffmpeg = Converter.FindFfmpeg();
        if (ffmpeg == null)
            Console.WriteLine("ffmpeg isn't on PATH: the exports weren't tested");
        else
        {
            int before = checks;
            TestConversions(ffmpeg);
            Console.WriteLine($"{checks - before} exports checked, with {ffmpeg}");
        }
        Console.WriteLine($"{(errors != 0 ? "FAILED" : "all good")} ({errors} errors)");
        return errors != 0 ? 1 : 0;
    }
}
