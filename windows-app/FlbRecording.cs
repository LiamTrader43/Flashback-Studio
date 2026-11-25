using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FlashbackStudio
{
    // What an export takes its pictures, sound and inputs from (see Converter):
    // a clip (FlbClip), or a recording's parts, one after another (FlbRecording).
    // Disposing it closes its files.
    interface IFlbVideo : IDisposable
    {
        int Width { get; }              // the top screen's frames
        int Height { get; }
        int BottomWidth { get; }        // the bottom screen's (0: none)
        int BottomHeight { get; }
        bool HasBottom { get; }
        int Fps { get; }
        int FrameCount { get; }         // frames, copies included
        bool HasAudio { get; }
        FlbInput[] Inputs { get; }      // the inputs during each frame, or null

        // Frames firstFrame .. firstFrame + frameCount - 1, in order: each
        // stored moment's pictures (the same arrays each time) and how many of
        // those frames it's shown for (see FlbClip.Moments).
        IEnumerable<(ushort[] Top, ushort[] Bottom, int Copies)> Moments(int firstFrame, int frameCount, bool bottom = true);

        // The sound of frames firstFrame .. firstFrame + frameCount - 1, to a WAV file.
        void WriteWav(string wav, int firstFrame, int frameCount);
    }

    // A recording: what Luma3DS-Flashback records with the clip length "until
    // stopped", in files of up to 10 minutes each, its parts: <name>_part01.flb,
    // <name>_part02.flb, and so on, each a clip of its own. Each part goes on
    // from where the one before ends, with no frame or sound left out or twice,
    // and starts with both screens' frames whole, so read one after another,
    // they're one video.
    sealed class FlbRecording : IFlbVideo
    {
        static readonly Regex PartName = new Regex(@"^(.+)_part(\d{2,})\.flb$", RegexOptions.IgnoreCase);

        // Whether the file 'path' is named as a recording's part: then the
        // recording's name (the file's, without "_partNN.flb") and the part's
        // number (1 for the first).
        public static bool IsPart(string path, out string name, out int number)
        {
            Match m = PartName.Match(Path.GetFileName(path));
            name = m.Success ? m.Groups[1].Value : null;
            number = 0;
            return m.Success && int.TryParse(m.Groups[2].Value, out number) && number > 0;
        }

        // The recordings among 'paths' (files in one folder): for each one with
        // more than one part, its parts in order, from the first on, as long as
        // they follow one another (a part missing ends it there). Keyed by each
        // part's path.
        public static Dictionary<string, List<string>> Group(IEnumerable<string> paths)
        {
            Dictionary<string, List<string>> parts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
                if (IsPart(p, out string name, out int number))
                {
                    if (!byName.TryGetValue(name, out SortedDictionary<int, string> numbered))
                        byName[name] = numbered = new SortedDictionary<int, string>();
                    numbered[number] = p;
                }
            foreach (SortedDictionary<int, string> numbered in byName.Values)
            {
                List<string> run = new List<string>();
                for (int n = 1; numbered.TryGetValue(n, out string p); n++)
                    run.Add(p);
                if (run.Count > 1)
                    foreach (string p in run)
                        parts[p] = run;
            }
            return parts;
        }

        // The recording's name, for a part of it (its file name without "_partNN.flb").
        public static string NameOf(string partPath) =>
            IsPart(partPath, out string name, out _) ? name : Path.GetFileNameWithoutExtension(partPath);

        readonly FlbClip[] parts;
        readonly int[] starts;          // the recording's frame each part starts at

        public int Width => parts[0].Width;
        public int Height => parts[0].Height;
        public int BottomWidth => parts[0].BottomWidth;
        public int BottomHeight => parts[0].BottomHeight;
        public bool HasBottom => parts[0].HasBottom;
        public int Fps => parts[0].Fps;
        public int FrameCount { get; }
        public bool HasAudio { get; }
        public FlbInput[] Inputs { get; }
        public int PartCount => parts.Length;

        FlbRecording(FlbClip[] parts)
        {
            this.parts = parts;
            starts = new int[parts.Length];
            int frames = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                starts[i] = frames;
                frames += parts[i].FrameCount;
            }
            FrameCount = frames;
            HasAudio = parts.All(p => p.HasAudio);
            Inputs = parts.All(p => p.Inputs != null) ? parts.SelectMany(p => p.Inputs).ToArray() : null;
        }

        // Opens a recording's parts, in order (Group's). Throws
        // InvalidDataException, saying which part and what's wrong, if one
        // can't be read, or wasn't recorded as the first was.
        public static FlbRecording Open(IList<string> paths)
        {
            List<FlbClip> parts = new List<FlbClip>();
            try
            {
                foreach (string path in paths)
                {
                    FlbClip clip;
                    try
                    {
                        clip = FlbClip.Open(path);
                    }
                    catch (InvalidDataException e)
                    {
                        throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}");
                    }
                    parts.Add(clip);
                    FlbClip first = parts[0];
                    if (clip.Width != first.Width || clip.Height != first.Height || clip.BottomWidth != first.BottomWidth ||
                        clip.BottomHeight != first.BottomHeight || clip.Fps != first.Fps || clip.HasAudio != first.HasAudio ||
                        clip.AudioRateMhz != first.AudioRateMhz)
                        throw new InvalidDataException($"{Path.GetFileName(path)} wasn't recorded with the same settings as {Path.GetFileName(first.Path)}");
                }
                if (parts.Count == 0)
                    throw new InvalidDataException("a recording without its files");
                return new FlbRecording(parts.ToArray());
            }
            catch
            {
                foreach (FlbClip clip in parts)
                    clip.Dispose();
                throw;
            }
        }

        public IEnumerable<(ushort[] Top, ushort[] Bottom, int Copies)> Moments(int firstFrame, int frameCount, bool bottom = true)
        {
            int end = firstFrame + frameCount;
            for (int i = 0; i < parts.Length && starts[i] < end; i++)
            {
                int from = Math.Max(firstFrame, starts[i]), to = Math.Min(end, starts[i] + parts[i].FrameCount);
                if (to > from)
                    foreach ((ushort[] Top, ushort[] Bottom, int Copies) m in parts[i].Moments(from - starts[i], to - from, bottom))
                        yield return m;
            }
        }

        // Each part's sound for its frames, one after another. Where they go
        // on into the next part, a part's sound goes on up to the sample that
        // part's first frame starts at, which it holds too, at the same place
        // in its ring (Flashback writes the sound around where a part starts
        // to both, and each part's ring is as big, so a sample has the same
        // place in each): so it goes on without a gap or anything twice, as
        // the game played it. (A frame's capture can be a few ms early or late,
        // which a part's frames, counted at the frame rate, don't show.) Its
        // last frames, or parts whose sound doesn't join that way, have as many
        // samples as they last.
        public void WriteWav(string wav, int firstFrame, int frameCount)
        {
            FlbClip first = parts[0];
            List<(FlbClip Part, long Start, long Count)> pieces = new List<(FlbClip, long, long)>();
            int end = firstFrame + frameCount;
            for (int i = 0; i < parts.Length && HasAudio; i++)
            {
                int from = Math.Max(firstFrame, starts[i]), to = Math.Min(end, starts[i] + parts[i].FrameCount);
                if (to <= from)
                    continue;
                long start = parts[i].AudioPosOf(from - starts[i]), n = parts[i].AudioSamplesIn(to - from);
                if (to < end && i + 1 < parts.Length)
                    n += JoinOffset(parts[i], start + n, parts[i + 1]);
                pieces.Add((parts[i], start, n));
            }

            using (FileStream o = new FileStream(wav, FileMode.Create, FileAccess.Write))
            {
                byte[] header = FlbClip.WavHeader(pieces.Sum(p => p.Count), first.AudioRateMhz);
                o.Write(header, 0, header.Length);
                short[] samples = new short[65536 * 2];
                byte[] buf = new byte[65536 * 4];
                foreach ((FlbClip part, long start, long n) in pieces)
                    for (long done = 0; done < n; )
                    {
                        int k = (int)Math.Min(n - done, 65536);
                        part.ReadAudio(start + done, k, samples);
                        Buffer.BlockCopy(samples, 0, buf, 0, k * 4);
                        o.Write(buf, 0, k * 4);
                        done += k;
                    }
            }
        }

        // How many samples past 'pos' (where 'part''s frames' sound ends,
        // counted at the frame rate) the sample 'next''s first frame starts at
        // is, in 'part''s ring: the nearest place that's at the same place in
        // the ring. 0 if the two rings aren't as big, or that's over a
        // quarter of a second away (they aren't one recording's parts).
        static long JoinOffset(FlbClip part, long pos, FlbClip next)
        {
            long cap = part.AudioCapacity;
            if (cap == 0 || cap != next.AudioCapacity)
                return 0;
            long d = ((next.AudioPosOf(0) - pos) % cap + cap) % cap;
            if (d > cap / 2)
                d -= cap;
            return Math.Abs(d) * 4000 > part.AudioRateMhz ? 0 : d;
        }

        public void Dispose()
        {
            foreach (FlbClip clip in parts)
                clip.Dispose();
        }
    }
}
