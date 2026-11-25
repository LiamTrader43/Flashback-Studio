// Recordings (windows-app/FlbRecording.cs): what Luma3DS-Flashback records
// "until stopped", in parts named <name>_part01.flb, _part02.flb, ... A
// recording's parts must read as one: its frames one part's after another's,
// each showing its own picture, the inputs likewise, and the sound each part's
// for its frames, adding up to exactly as many samples as the frames last,
// from any frame to any other. Parts are found by their names (a part missing
// ends a recording there), and one recorded with other settings is turned
// down. And exported, the MP4 holds every frame of every part, with its sound.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FlashbackStudio;

static partial class WindowsAppTest
{
    // Three parts of a recording (with sound and inputs, as recorded and
    // compacted), each with pictures of its own. (Their sound rings aren't as
    // big, so their sound doesn't join: each part's lasts as long as its
    // frames. For sound that joins, see TestJoinedSound.)
    static readonly Spec[] RecordingParts =
    {
        new Spec { Copies = new[] { 1, 3, 1, 2, 1, 1 }, Audio = true, Inputs = true, Pictures = 0 },
        new Spec { Copies = new[] { 2, 1, 1, 4, 1, 1 }, Audio = true, Inputs = true, Pictures = 100 },
        new Spec { Copies = new[] { 1, 1, 2, 1, 3, 1, 1 }, Audio = true, Inputs = true, Compacted = true, Pictures = 200 },
    };
    static readonly string[] RecordingFiles = { "rec_test_part01.flb", "rec_test_part02.flb", "rec_test_part03.flb" };

    // What each frame of the recording shows: its part's entry's picture number.
    static int[] RecordingPictures() =>
        RecordingParts.SelectMany(s => EntryOfFrame(s).Select(e => s.Pictures + e)).ToArray();

    static void TestRecordings()
    {
        for (int i = 0; i < RecordingParts.Length; i++)
            Write(RecordingFiles[i], RecordingParts[i]);
        int total = RecordingParts.Sum(s => s.Frames);
        int[] pictures = RecordingPictures();

        // Found by their names: a part missing ends a recording there, and a
        // part on its own isn't one.
        string[] names = { "rec_test_part01.flb", "rec_test_part02.flb", "rec_test_part03.flb", "rec_one_part01.flb",
                           "rec_gap_part01.flb", "rec_gap_part03.flb", "clip_2026.flb", "rec_late_part02.flb", "rec_late_part03.flb",
                           "REC_Case_Part01.FLB", "REC_Case_Part02.flb" };
        Dictionary<string, List<string>> found = FlbRecording.Group(names);
        checks++;
        if (found.Count != 5 || !found.TryGetValue("rec_test_part02.flb", out List<string> run) ||
            !run.SequenceEqual(new[] { "rec_test_part01.flb", "rec_test_part02.flb", "rec_test_part03.flb" }) ||
            !found.TryGetValue("REC_Case_Part02.flb", out List<string> caseRun) || caseRun.Count != 2 ||
            found.ContainsKey("rec_one_part01.flb") || found.ContainsKey("rec_gap_part01.flb") || found.ContainsKey("rec_late_part02.flb"))
            Fail($"recordings found by name: {string.Join(", ", found.Keys)}");
        if (FlbRecording.NameOf("dir\\rec_2026-11-23_14-00-00.000_part12.flb") != "rec_2026-11-23_14-00-00.000" ||
            FlbRecording.NameOf("clip.flb") != "clip")
            Fail("a recording's name");

        using (FlbRecording r = FlbRecording.Open(RecordingFiles))
        {
            checks++;
            if (r.FrameCount != total || r.PartCount != 3 || r.Width != 8 || r.Height != 6 || r.Fps != Fps || !r.HasAudio || r.HasBottom)
                Fail($"the recording: {r.FrameCount} frames in {r.PartCount} parts, {r.Width}x{r.Height}");

            // its inputs: each part's, one after another
            FlbInput[] inputs = RecordingParts.SelectMany(s => Enumerable.Range(0, s.Frames).Select(InputOf)).ToArray();
            if (r.Inputs == null || !r.Inputs.SequenceEqual(inputs))
                Fail("the recording's inputs");

            // its frames, from any frame to any other
            ushort[] want = new ushort[8 * 6];
            for (int first = 0; first < total; first += 3)
                for (int count = 1; first + count <= total; count += first % 2 == 0 ? 1 : 5)
                {
                    int f = first;
                    bool bad = false;
                    foreach ((ushort[] top, ushort[] bottom, int copies) in r.Moments(first, count))
                    {
                        for (int c = 0; c < copies && !bad; c++, f++)
                        {
                            if (f < first + count)
                                for (int i = 0; i < want.Length; i++)
                                    want[i] = PixelOf(pictures[f], i % 8, i / 8);
                            bad = f >= first + count || !top.SequenceEqual(want) || bottom != null;
                        }
                        if (bad)
                            break;
                    }
                    checks++;
                    if (bad || f != first + count)
                        Fail($"the recording's frames {first} to {first + count - 1}: wrong at frame {f - (bad ? 1 : 0)}");
                }

            // its sound: each part's, for its frames, as many samples in all as the frames last
            foreach ((int first, int count) in new[] { (0, total), (4, 10), (8, 12), (0, 1), (total - 1, 1), (9, total - 9) })
            {
                checks++;
                string wav = "rec_test.wav";
                r.WriteWav(wav, first, count);
                byte[] got = File.ReadAllBytes(wav);
                File.Delete(wav);
                List<short> expected = new List<short>();
                long Samples(int frames) => (frames * RateMhz / Fps + 500) / 1000;
                int start = 0;
                for (int i = 0; i < RecordingParts.Length; i++)
                {
                    int from = Math.Max(first, start), to = Math.Min(first + count, start + RecordingParts[i].Frames);
                    if (to > from)
                        using (FlbClip part = FlbClip.Open(RecordingFiles[i]))
                        {
                            int n = (int)Samples(to - from);
                            short[] samples = new short[n * 2];
                            part.ReadAudio(part.AudioPosOf(from - start), n, samples);
                            expected.AddRange(samples);
                        }
                    start += RecordingParts[i].Frames;
                }
                long length = BitConverter.ToUInt32(got, 40) / 4;
                if (length * 2 != expected.Count || got.Length != 44 + length * 4)
                    Fail($"the recording's sound, frames {first} to {first + count - 1}: {length} samples, want {expected.Count / 2}");
                else
                    for (int k = 0; k < expected.Count; k++)
                        if (BitConverter.ToInt16(got, 44 + k * 2) != expected[k])
                        {
                            Fail($"the recording's sound, frames {first} to {first + count - 1}: sample {k / 2}");
                            break;
                        }
            }
        }

        // A part recorded with other settings (here, at another size) is turned down.
        checks++;
        Write("rec_odd_part01.flb", RecordingParts[0]);
        Write("rec_odd_part02.flb", new Spec { Copies = new[] { 1, 2 }, Audio = true, Inputs = true, Width = 10 });
        try
        {
            using (FlbRecording.Open(new[] { "rec_odd_part01.flb", "rec_odd_part02.flb" }))
                Fail("a recording with a part at another size was opened");
        }
        catch (InvalidDataException e)
        {
            if (!e.Message.Contains("rec_odd_part02.flb"))
                Fail($"a recording with a part at another size: \"{e.Message}\"");
        }
        Console.WriteLine($"recordings: {total} frames in {RecordingParts.Length} parts read as one");
        TestJoinedSound();
    }

    // Parts as Flashback records them: one stream of sound (sample i is Left(i),
    // Right(i)), each part's ring as big, holding it from a little before its
    // first frame to a little after the next part's first frame, which was
    // captured a few ms before or after its frames, counted at the frame rate,
    // end. Their sound, from any frame to any other, must go on without a
    // gap or a sample twice: from the first frame's sample, up to each next
    // part's first frame's, then for as long as the last frames last.
    static void TestJoinedSound()
    {
        int[][] copies = { new[] { 3, 1, 2, 2, 1 }, new[] { 1, 4, 1, 1, 2, 1 }, new[] { 2, 2, 1, 1 } };
        long[] jitter = { 0, 41, -29 };     // (where each part's first frame is, against where the frames before end)
        long Samples(int frames) => (frames * RateMhz / Fps + 500) / 1000;
        string[] files = new string[copies.Length];
        long[] trackStart = new long[copies.Length];
        int[] frameCount = new int[copies.Length];
        long t = 7_000_123, capacity = 40_000;
        for (int i = 0; i < copies.Length; i++)
        {
            frameCount[i] = copies[i].Sum();
            trackStart[i] = t + jitter[i];
            t = trackStart[i] + Samples(frameCount[i]);
        }
        for (int i = 0; i < copies.Length; i++)
        {
            long nextStart = i + 1 < copies.Length ? trackStart[i + 1] : trackStart[i] + Samples(frameCount[i]);
            files[i] = $"rec_joined_part{i + 1:00}.flb";
            Write(files[i], new Spec { Copies = copies[i], Audio = true, Inputs = true, Pictures = 300 + 100 * i, AudioGiven = true,
                                       Capacity = capacity, TrackStart = trackStart[i], First = trackStart[i] - 300,
                                       End = nextStart + (i + 1 < copies.Length ? 500 : 0) });
        }
        int total = frameCount.Sum();
        using (FlbRecording r = FlbRecording.Open(files))
            foreach ((int first, int count) in new[] { (0, total), (2, 14), (frameCount[0] - 1, 2), (frameCount[0], frameCount[1] + 3), (5, 3) })
            {
                checks++;
                string wav = "rec_joined.wav";
                r.WriteWav(wav, first, count);
                byte[] got = File.ReadAllBytes(wav);
                File.Delete(wav);
                // (where the first frame's sound starts, and the part the last frame is in, and where in it)
                int part = 0, local = first;
                while (local >= frameCount[part]) local -= frameCount[part++];
                long from;
                using (FlbClip c = FlbClip.Open(files[part]))
                    from = c.AudioPosOf(local);
                int lastPart = 0, lastLocal = first + count;
                while (lastPart + 1 < copies.Length && lastLocal > frameCount[lastPart]) lastLocal -= frameCount[lastPart++];
                long to = lastPart == part ? from + Samples(count) : trackStart[lastPart] + Samples(lastLocal);
                long length = BitConverter.ToUInt32(got, 40) / 4;
                if (length != to - from || got.Length != 44 + length * 4)
                {
                    Fail($"joined sound, frames {first} to {first + count - 1}: {length} samples, want {to - from}");
                    continue;
                }
                for (long k = 0; k < length; k++)
                    if (BitConverter.ToInt16(got, 44 + (int)k * 4) != Left(from + k) || BitConverter.ToInt16(got, 46 + (int)k * 4) != Right(from + k))
                    {
                        Fail($"joined sound, frames {first} to {first + count - 1}: sample {k} isn't the one after the one before");
                        break;
                    }
            }
        Console.WriteLine("recordings: sound across where parts start, joined sample by sample");
    }

    // A recording exported as one MP4: every frame of every part, at twice the
    // size, and its sound; and part of it, across where a part starts.
    static void TestRecordingConversions(string ffmpeg)
    {
        int total = RecordingParts.Sum(s => s.Frames), size = 16 * 12 * 3;
        int[] pictures = RecordingPictures();
        foreach ((int first, int count) in new[] { (0, total), (RecordingParts[0].Frames - 3, 8) })
        {
            checks++;
            string what = $"a recording's frames {first} to {first + count - 1}", mp4 = "rec_test.mp4";
            try
            {
                using (FlbRecording r = FlbRecording.Open(RecordingFiles))
                    Converter.ToMp4(r, first, count, mp4, null, ExportSize.Double, ExportScaling.Nearest, ExportEncoder.Cpu, ffmpeg,
                                    done => { }, CancellationToken.None);
            }
            catch (Exception x)
            {
                Fail($"{what}: {x.Message}");
                continue;
            }
            byte[] video = Run(ffmpeg, $"-v error -i \"{mp4}\" -f rawvideo -pix_fmt bgr24 -").Out;
            if (video.Length != count * size)
            {
                Fail($"{what}: {video.Length / size} frames, want {count}");
                continue;
            }
            double worst = 99;
            for (int v = 0; v < count; v++)
            {
                int f = first + v;
                double right = Psnr(video, v * size, Bgr((x, y) => PixelOf(pictures[f], x / 2, y / 2), 16, 12), size);
                worst = Math.Min(worst, right);
                if (right < 30)
                {
                    Fail($"{what}: frame {f} is {right:F1} dB from what it should be");
                    break;
                }
            }
            var audio = Run(ffmpeg, $"-v error -i \"{mp4}\" -map 0:a -f s16le -ac 2 -ar 48000 -");
            long samples = audio.Out.Length / 4, wantSamples = (long)count * 48000 / Fps;
            if (audio.Exit != 0 || Math.Abs(samples - wantSamples) > 1024)
                Fail($"{what}: {samples} sound samples, want about {wantSamples}");
            Console.WriteLine($"  {what}: {count} frames, each within {worst:F1} dB (PSNR) of its part's");
            File.Delete(mp4);
        }
    }
}
