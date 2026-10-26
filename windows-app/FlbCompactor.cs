using System;
using System.IO;

namespace FlashbackStudio
{
    // Compacting and trimming, as the 3DS app's flb.c does them (keep the two
    // alike: tests/ checks they write the same bytes). The clip's frames (all of
    // them, or the ones kept), their sound and their inputs are copied into
    // "<copy>.tmp", a few at a time (so the caller can show progress), which then
    // replaces the clip (compacting) or becomes a new clip (trimming). The clip
    // itself is only touched at the very end of a compaction, and is left as it
    // was if anything fails or it's cancelled.
    //
    // Failures throw InvalidDataException, with flb.c's messages.
    sealed class FlbCompactor
    {
        const uint Magic = 0x4B424C46;          // "FLBK"
        const uint InputMagic = 0x49424C46;     // "FLBI"

        readonly string path, outPath, tmpPath;     // the clip, the copy (the clip itself when compacting), and the copy until it's finished
        FlbClip clip;
        FileStream output;
        readonly int firstFrame, frameCount;        // the frames copied
        readonly int firstEntry, endEntry;          // the index entries they use
        readonly long[] offsets;                    // each copied entry's offset in the copy
        int next;                                   // the next entry to copy
        readonly long audioStart, audioCount;       // the sound track: copied after the frames
        long audioDone, audioOffset;
        readonly byte[] stored;

        public long Copied { get; private set; }    // bytes copied so far...
        public long Total { get; }                  // ...and in all

        static int EntrySize(FlbClip c) => c.HasAudio ? 16 : 12;
        static int AudioInfoSize(FlbClip c) => c.HasAudio ? FlbClip.AudioInfoSize : 0;
        static long InputsSize(FlbClip c, int frames) =>
            c.Inputs != null ? FlbClip.InputHeaderSize + (long)frames * FlbClip.InputRecordSize : 0;

        // The index entries frames firstFrame .. firstFrame + frameCount - 1 use.
        static void EntryRange(FlbClip c, int firstFrame, int frameCount, out int first, out int end)
        {
            first = c.EntryForFrame(firstFrame);
            end = c.EntryForFrame(firstFrame + frameCount - 1) + 1;
        }

        // How many of those frames entry 'e' is shown for (fewer than its copies
        // if the range starts or ends part way through them).
        static int CopiesIn(FlbClip c, int e, int firstFrame, int frameCount)
        {
            FlbClip.Entry entry = c[e];
            int from = Math.Max(entry.FirstFrame, firstFrame), to = Math.Min(entry.FirstFrame + entry.Copies, firstFrame + frameCount);
            return to - from;
        }

        // The size of frames firstFrame .. firstFrame + frameCount - 1 of a clip, trimmed (as a compacted clip).
        public static long TrimSize(FlbClip c, int firstFrame, int frameCount)
        {
            EntryRange(c, firstFrame, frameCount, out int first, out int end);
            long size = (long)(end - first) * EntrySize(c) + AudioInfoSize(c) + FlbClip.TrailerSize + InputsSize(c, frameCount);
            for (int e = first; e < end; e++)
                size += c[e].Size;
            if (c.HasAudio)
                size += c.AudioSamplesIn(frameCount) * 4;
            return size;
        }

        // The size of a clip once compacted.
        public static long CompactSize(FlbClip c) => TrimSize(c, 0, c.FrameCount);

        // Starts compacting the clip at 'path'.
        public static FlbCompactor Compact(string path) => new FlbCompactor(path, path, 0, -1);

        // Starts copying frames firstFrame .. firstFrame + frameCount - 1 of the
        // clip at 'path', with their sound and inputs, into a new clip at
        // 'outPath' (which mustn't exist).
        public static FlbCompactor Trim(string path, string outPath, int firstFrame, int frameCount)
        {
            if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the trimmed clip needs a name of its own");
            return new FlbCompactor(path, outPath, firstFrame, frameCount);
        }

        bool Compacting => string.Equals(path, outPath, StringComparison.Ordinal);

        // (frameCount -1: all of them)
        FlbCompactor(string path, string outPath, int firstFrame, int frameCount)
        {
            this.path = path;
            this.outPath = outPath;
            tmpPath = outPath + ".tmp";
            clip = FlbClip.Open(path);
            try
            {
                if (clip.IsRaw)
                    throw new InvalidDataException("clips from older Flashback builds (.raw) can't be compacted or trimmed");
                if (Compacting && clip.IsCompacted)
                    throw new InvalidDataException("this clip is already compacted");
                if (frameCount == -1)
                    frameCount = clip.FrameCount;
                if (frameCount <= 0 || firstFrame < 0 || firstFrame >= clip.FrameCount || frameCount > clip.FrameCount - firstFrame)
                    throw new InvalidDataException("those frames aren't in the clip");
                this.firstFrame = firstFrame;
                this.frameCount = frameCount;
                EntryRange(clip, firstFrame, frameCount, out firstEntry, out endEntry);
                next = firstEntry;

                // Just the sound the frames last, from where the first one's starts
                // (its entry's, plus the copies of it skipped): readers play a
                // clip's sound straight on from its first entry's position, which
                // is then 0.
                if (clip.HasAudio)
                {
                    audioStart = clip.AudioPosOf(firstFrame);
                    audioCount = clip.AudioSamplesIn(frameCount);
                }
                long size = TrimSize(clip, firstFrame, frameCount);
                Total = size - (long)(endEntry - firstEntry) * EntrySize(clip) - AudioInfoSize(clip) - FlbClip.TrailerSize -
                        InputsSize(clip, frameCount);
                offsets = new long[endEntry - firstEntry];
                stored = new byte[clip.SlotBytes];

                // A trimmed clip is a new one: never write over another.
                if (!Compacting && File.Exists(outPath))
                    throw new InvalidDataException("there's already a clip with that name");
                try
                {
                    output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 512 * 1024);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new InvalidDataException("the copy couldn't be created");
                }
                // The copy at its full size up front: its space is claimed once,
                // instead of the file growing with every block.
                try
                {
                    output.SetLength(size);
                }
                catch (IOException)
                {
                    Cancel();
                    throw new InvalidDataException("the copy couldn't be created (is the disk full?)");
                }
            }
            catch
            {
                clip?.Dispose();
                clip = null;
                throw;
            }
        }

        // Stops and deletes the copy; the clip is left as it was.
        public void Cancel()
        {
            output?.Dispose();
            output = null;
            clip?.Dispose();
            clip = null;
            try
            {
                File.Delete(tmpPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        void Put(byte[] src, int len)
        {
            try
            {
                output.Write(src, 0, len);
            }
            catch (IOException)
            {
                throw new InvalidDataException("the copy couldn't be written (is the disk full?)");
            }
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

        // Copies the next frame (or block of sound). Returns false while there's
        // more to do, and true once the copy is finished (the clip replaced by its
        // compacted copy, or the trimmed clip saved). On failure the copy is
        // deleted, the clip left as it was, and it throws.
        public bool Step()
        {
            try
            {
                if (next < endEntry)
                {
                    FlbClip.Entry e = clip[next];
                    clip.ReadStored(next, stored);
                    Put(stored, e.Size);
                    offsets[next++ - firstEntry] = Copied;
                    Copied += e.Size;
                    return false;
                }
                if (audioDone < audioCount)
                {
                    if (audioDone == 0)
                        audioOffset = Copied;
                    int n = (int)Math.Min(clip.SlotBytes / 4, audioCount - audioDone);
                    short[] samples = new short[n * 2];
                    clip.ReadAudio(audioStart + audioDone, n, samples);
                    Buffer.BlockCopy(samples, 0, stored, 0, n * 4);
                    Put(stored, n * 4);
                    Copied += n * 4;
                    audioDone += n;
                    return false;
                }
                Finish();
                return true;
            }
            catch
            {
                Cancel();
                throw;
            }
        }

        // Writes the inputs of the frames copied (if the clip has inputs), the
        // index (sound info) and trailer, checks the copy reads back, then makes
        // it the clip. Compacting swaps it in for the clip: the clip is renamed
        // out of the way first, so there's always a whole clip (see
        // CleanUpLeftover). Trimming gives it its own name.
        void Finish()
        {
            int entries = endEntry - firstEntry, entrySize = EntrySize(clip);
            int inputSize = (int)InputsSize(clip, frameCount);
            int indexSize = entries * entrySize + AudioInfoSize(clip);
            byte[] tail = new byte[inputSize + indexSize + FlbClip.TrailerSize];

            long inputOffset = 0;
            if (inputSize != 0)
            {
                inputOffset = Copied;
                Put32(tail, 0, InputMagic);
                Put32(tail, 4, 1);
                Put32(tail, 8, frameCount);
                Put32(tail, 12, FlbClip.InputRecordSize);
                for (int f = 0; f < frameCount; f++)
                {
                    FlbInput n = clip.Inputs[firstFrame + f];
                    int p = FlbClip.InputHeaderSize + f * FlbClip.InputRecordSize;
                    Put32(tail, p, n.Buttons);
                    Put16(tail, p + 4, n.CircleX);
                    Put16(tail, p + 6, n.CircleY);
                    Put16(tail, p + 8, n.TouchX);
                    Put16(tail, p + 10, n.TouchY);
                    Put16(tail, p + 12, n.CStickX);
                    Put16(tail, p + 14, n.CStickY);
                }
            }

            int index = inputSize;
            for (int i = 0; i < entries; i++)
            {
                int e = firstEntry + i, p = index + i * entrySize;
                FlbClip.Entry entry = clip[e];
                Put32(tail, p, offsets[i]);
                Put32(tail, p + 4, entry.Size);
                Put32(tail, p + 8, CopiesIn(clip, e, firstFrame, frameCount));
                if (clip.HasAudio)
                {
                    // The copy's sound starts at its first frame, and its first
                    // entry may be kept from part way through its copies.
                    int skipped = entry.FirstFrame < firstFrame ? firstFrame - entry.FirstFrame : 0;
                    long pos = entry.AudioPos + clip.AudioOffsetOf(skipped);
                    Put32(tail, p + 12, pos > audioStart ? pos - audioStart : 0);
                }
            }
            if (clip.HasAudio)
            {
                int a = index + entries * entrySize;
                Put32(tail, a, clip.AudioRateMhz);
                tail[a + 4] = 2;        // channels
                tail[a + 6] = 16;       // bits per sample
                Put32(tail, a + 8, audioOffset);
                Put32(tail, a + 12, audioCount);
                Put32(tail, a + 16, 0);
                Put32(tail, a + 20, audioCount);
                Put32(tail, a + 24, clip.AudioMissed);
            }
            int t = index + indexSize;
            Put32(tail, t, Magic);
            Put32(tail, t + 4, clip.HasAudio ? 3 : 2);
            Put32(tail, t + 8, clip.Width);
            Put32(tail, t + 12, clip.Height);
            Put32(tail, t + 16, clip.Fps);
            Put32(tail, t + 20, 0);     // no slots
            Put32(tail, t + 24, clip.SlotBytes);
            Put32(tail, t + 28, entries);
            Put32(tail, t + 32, frameCount);
            Put32(tail, t + 36, Copied + inputSize);
            Put32(tail, t + 40, inputOffset);

            Put(tail, tail.Length);
            Copied += inputSize;
            try
            {
                output.Dispose();
            }
            catch (IOException)
            {
                throw new InvalidDataException("the copy couldn't be written (is the disk full?)");
            }
            output = null;

            // Check the copy reads back as a whole clip before it's used (its inputs too).
            using (FlbClip check = FlbClip.Open(tmpPath))
                if (check.FrameCount != frameCount || check.EntryCount != entries ||
                    check.FileSize != Copied + indexSize + FlbClip.TrailerSize || (check.Inputs != null) != (inputSize != 0))
                    throw new InvalidDataException("the copy didn't read back correctly");

            clip.Dispose();     // (it can't be renamed while open)
            clip = null;
            if (!Compacting)
            {
                if (!TryMove(tmpPath, outPath))
                    throw new InvalidDataException("the trimmed clip couldn't be saved");
                return;
            }
            string oldPath = path + ".old";
            if (!TryMove(path, oldPath))
                throw new InvalidDataException("the clip couldn't be replaced");
            if (!TryMove(tmpPath, path))
            {
                TryMove(oldPath, path);
                throw new InvalidDataException("the clip couldn't be replaced");
            }
            TryDelete(oldPath);
        }

        static bool TryMove(string from, string to)
        {
            try
            {
                File.Move(from, to);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        // Cleans up after a compaction that was cut off (the PC turned off, for
        // example) while the copy was replacing the clip. 'file' is a file in the
        // clip folder: if it's a leftover "<clip>.tmp" or "<clip>.old", it's
        // deleted, or renamed back to the clip if the clip itself is missing.
        // Returns true if 'file' was such a leftover.
        public static bool CleanUpLeftover(string file)
        {
            if (file.EndsWith(".flb.tmp", StringComparison.Ordinal))
            {
                TryDelete(file);    // an unfinished copy
                return true;
            }
            if (!file.EndsWith(".flb.old", StringComparison.Ordinal))
                return false;
            // The clip, renamed out of the way while its copy replaced it.
            string clip = file.Substring(0, file.Length - 4);
            if (File.Exists(clip))
                TryDelete(file);    // the copy made it: the original isn't needed
            else
                TryMove(file, clip);    // it didn't: put the original back
            return true;
        }
    }
}
