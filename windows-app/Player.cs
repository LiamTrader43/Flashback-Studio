using System;
using System.Diagnostics;
using System.IO;

namespace FlashbackStudio
{
    // A clip open in the player, as in the 3DS app (main.c's Player): the frame
    // on screen, playing or paused at one of four speeds, looping or not, the
    // trimmed part, and the clip's sound, which plays at 1x and keeps the picture
    // in step with it.
    sealed class Player : IDisposable
    {
        public static readonly int[] SpeedQuarters = { 1, 2, 4, 8 };
        public static readonly string[] SpeedNames = { "1/4x", "1/2x", "1x", "2x" };
        public const int SpeedNormal = 2;
        const int NudgeMs = 15;     // move the picture to the sound if they're further apart than this...
        const int RestartMs = 300;  // ...or restart the sound if they're further apart than this

        public readonly FlbClip Clip;
        public readonly string Title;
        public int Frame;                   // clip frame on screen (copies counted)
        public int ShownEntry = -1;         // index entry in Pixels, or -1
        public readonly ushort[] Pixels;    // the frame on screen, decoded
        readonly ushort[] decoded;
        public bool Playing = true, Loop, ShowInputs, ShowStats;
        public int Speed = SpeedNormal;
        public string Error;                // why the clip can't be read further
        public bool SettingsOpen;
        public int SettingsItem;
        public double DecodeMs, DrawMs;     // last frame's read + decode, and drawing, times
        public int Skipped;                 // stored frames passed over without being shown

        // Trimming: frames TrimIn .. TrimOut are the selection, which plays (and
        // loops) on its own and can be saved as a new clip, or exported.
        public bool TrimMode;
        public int TrimIn, TrimOut;
        public string TrimMessage;

        // The sound: streamed from the clip through AudioOut's buffers.
        public bool Scrubbing;              // the bar is held (no sound meanwhile)
        public string AudioError;
        public int AudioOffsetMs;           // how far the sound is ahead of the picture (last check)
        public bool AudioOn => audioQueued;
        AudioOut audio;
        bool audioQueued, clockMoved;       // (the picture's clock was set: a jump, a loop, play: restart the sound)
        long audioNext, audioFirst;         // the next sample to queue, and where the sound started (counting from the clip's first frame)
        long audioCheck;
        readonly short[] audioBuf = new short[AudioOut.BufferSamples * 2];

        long baseTick;                      // the clock: Frame = baseFrame + time since baseTick
        int baseFrame;

        public Player(string path, bool loop, bool showInputs, bool showStats)
        {
            Clip = FlbClip.Open(path);
            Title = Names.Pretty(System.IO.Path.GetFileName(path));
            Pixels = new ushort[Clip.Width * Clip.Height];
            decoded = new ushort[Pixels.Length];
            Loop = loop;
            ShowInputs = showInputs;
            ShowStats = showStats;
            TrimOut = Clip.FrameCount - 1;
            Rebase();
        }

        public void Dispose()
        {
            audio?.Dispose();
            Clip.Dispose();
        }

        public int Count => Clip.FrameCount;
        // What plays: the selection while trimming, otherwise the whole clip.
        int Lo => TrimMode ? TrimIn : 0;
        int Hi => TrimMode ? TrimOut + 1 : Count;

        void Rebase()
        {
            baseTick = Stopwatch.GetTimestamp();
            baseFrame = Frame;
            clockMoved = true;
        }

        int ClockFrame()
        {
            long elapsed = Stopwatch.GetTimestamp() - baseTick;
            return baseFrame + (int)(elapsed * Clip.Fps * SpeedQuarters[Speed] / (Stopwatch.Frequency * 4));
        }

        public void Seek(int frame)
        {
            Frame = Math.Max(0, Math.Min(Count - 1, frame));
            Rebase();
        }

        public void PlayPause()
        {
            if (Playing)
                Playing = false;
            else
            {
                if (Frame + 1 >= Hi || Frame < Lo)
                    Frame = Lo;
                Playing = true;
                Rebase();
            }
        }

        public void Step(int frames)
        {
            Playing = false;
            Seek(Frame + frames);
        }

        public void Jump(int seconds) => Seek(Frame + seconds * Clip.Fps);

        public void ChangeSpeed(int by)
        {
            int speed = Math.Max(0, Math.Min(SpeedQuarters.Length - 1, Speed + by));
            if (speed != Speed)
            {
                Speed = speed;
                Rebase();
            }
        }

        public void ToggleTrim()
        {
            TrimMode = !TrimMode;
            TrimMessage = null;
        }

        public void SetTrimIn()
        {
            TrimIn = Frame;
            TrimOut = Math.Max(TrimOut, TrimIn);
            TrimMessage = null;
        }

        public void SetTrimOut()
        {
            TrimOut = Frame;
            TrimIn = Math.Min(TrimIn, TrimOut);
            TrimMessage = null;
        }

        // The whole clip is selected (nothing trimmed off)?
        public bool TrimIsWhole => TrimIn == 0 && TrimOut + 1 == Count;

        // Whether clip frame 'frame' is a copy of the one before (a missed frame).
        public bool IsMissed(int frame)
        {
            FlbClip.Entry e = Clip[Clip.EntryForFrame(frame)];
            return frame > e.FirstFrame;
        }

        // Moves the clock on, and reads the frame that's now on screen if it's
        // another stored frame. Returns true if the picture changed (another
        // stored frame, or another frame's inputs).
        public bool Tick()
        {
            int before = Frame;
            if (Playing && Error == null)
            {
                int frame = ClockFrame(), lo = Lo, hi = Hi;
                if (frame >= hi)
                {
                    if (Loop)
                    {
                        Frame = lo + (frame - hi) % (hi - lo);
                        Rebase();
                    }
                    else
                    {
                        Frame = hi - 1;
                        Playing = false;
                    }
                }
                else
                    Frame = frame;
            }

            bool changed = Frame != before;
            int entry = Clip.EntryForFrame(Frame);
            if (entry != ShownEntry && Error == null)
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    // (Into a frame of its own: a damaged one leaves the last good one on screen.)
                    Clip.DecodeEntry(entry, decoded);
                    Array.Copy(decoded, Pixels, Pixels.Length);
                    DecodeMs = sw.Elapsed.TotalMilliseconds;
                    if (Playing && ShownEntry != -1 && entry > ShownEntry + 1)
                        Skipped += entry - ShownEntry - 1;
                    ShownEntry = entry;
                    changed = true;
                }
                catch (InvalidDataException e)
                {
                    Error = "This frame can't be read: " + e.Message + ".";
                    Playing = false;
                }
            }
            TickAudio();
            return changed;
        }

        // ---- Sound

        long AudioTotal => Clip.AudioSamplesIn(Count);

        // Where the picture is, in sound samples (at 1x speed, the only one with sound).
        long ClockSample()
        {
            long elapsed = Stopwatch.GetTimestamp() - baseTick;
            return Clip.AudioOffsetOf(baseFrame) + elapsed * Clip.AudioRateMhz / (Stopwatch.Frequency * 1000);
        }

        void AudioStop()
        {
            if (!audioQueued)
                return;
            audio.Stop();
            audioQueued = false;
        }

        void AudioFill(int i)
        {
            int n = (int)Math.Min(AudioOut.BufferSamples, AudioTotal - audioNext);
            try
            {
                Clip.ReadAudio(Clip[0].AudioPos + audioNext, n, audioBuf);
            }
            catch (InvalidDataException e)
            {
                AudioError = e.Message;
                AudioStop();
                return;
            }
            audio.Queue(i, audioBuf, n);
            audioNext += n;
        }

        // Starts the sound at sample 'sample'.
        void AudioStartAt(long sample)
        {
            audioNext = audioFirst = Math.Max(0, sample);
            audioQueued = true;
            for (int i = 0; i < AudioOut.Buffers && audioQueued && audioNext < AudioTotal; i++)
                AudioFill(i);
            // First check 0.1 s in, once it's surely playing.
            audioCheck = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 2 + Stopwatch.Frequency / 10;
        }

        // Plays the sound when the picture plays at 1x (not paused, stepping or
        // scrubbing), and refills played buffers. Shortly after the sound starts,
        // and twice a second after that, the picture's clock is moved to match it.
        // If they're far apart, the sound starts over at the right place instead.
        void TickAudio()
        {
            bool want = Clip.HasAudio && AudioError == null && Error == null && Playing && Speed == SpeedNormal && !Scrubbing;
            if (!want)
            {
                clockMoved = false;
                AudioStop();
                return;
            }
            if (audio == null)
            {
                try
                {
                    audio = new AudioOut((int)((Clip.AudioRateMhz + 500) / 1000));
                }
                catch (InvalidOperationException e)
                {
                    AudioError = e.Message;
                    return;
                }
            }
            if (!audioQueued || clockMoved)
            {
                clockMoved = false;
                AudioStop();
                AudioStartAt(ClockSample());
                return;
            }

            bool active = false;
            for (int i = 0; i < AudioOut.Buffers && audioQueued; i++)
            {
                if (audio.IsDone(i) && audioNext < AudioTotal)
                    AudioFill(i);
                active |= !audio.IsDone(i);
            }

            long now = Stopwatch.GetTimestamp();
            if (audioQueued && now - audioCheck >= Stopwatch.Frequency / 2)
            {
                audioCheck = now;
                long clock = ClockSample();
                if (active)
                {
                    long ahead = audioFirst + audio.Played - clock;     // samples the sound is ahead of the picture
                    AudioOffsetMs = (int)(ahead * 1000000 / Clip.AudioRateMhz);
                    if (Math.Abs(AudioOffsetMs) > RestartMs)
                    {
                        AudioStop();
                        AudioStartAt(clock);
                    }
                    else if (Math.Abs(AudioOffsetMs) > NudgeMs)
                        baseTick -= ahead * Stopwatch.Frequency * 1000 / Clip.AudioRateMhz;    // (not Rebase: the sound carries on)
                }
                else if (audioNext < AudioTotal)
                {
                    // Nothing queued with sound still to come: it ran dry. Start over.
                    AudioStop();
                    AudioStartAt(clock);
                }
            }
        }
    }
}
