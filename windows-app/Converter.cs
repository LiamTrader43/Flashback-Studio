using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FlashbackStudio
{
    // An exported video's size: twice the clip's, or 1080, 1440 or 2160 pixels
    // tall (keeping the 3DS screen's shape), or twice the clip's in a file
    // small enough to send on Discord (see DiscordLimit)...
    enum ExportSize { Double, P1080, P1440, P2160, Discord }

    // ...and how its frames are scaled up to it: each pixel copied (nearest:
    // sharp pixels), or smoothed, with ffmpeg's scaler...
    enum ExportScaling { Nearest, Bilinear, Bicubic, Lanczos, Spline }

    // ...and what encodes it: the CPU (x264), or an NVIDIA graphics card's
    // encoder (NVENC), much faster for big videos.
    enum ExportEncoder { Cpu, Gpu }

    // Exports clips as MP4 videos with ffmpeg: H.264 at the size asked for, at
    // the frame rate it was recorded at, with its sound as an AAC track, and the
    // input display drawn over it if asked. At twice the clip's size with
    // nearest (as exports always were), each pixel becomes 2 x 2 here;
    // otherwise a second ffmpeg scales the frames (its scale filter), and they
    // come back here, so the input display is drawn at the video's size and
    // stays sharp.
    static class Converter
    {
        // ffmpeg.exe next to this program (as it comes in the release), or else on
        // PATH; null if there's neither.
        public static string FindFfmpeg()
        {
            string here = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(here))
                return here;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string path = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(path))
                        return path;
                }
                catch (ArgumentException)
                {
                    // (a PATH entry that isn't a path)
                }
            }
            return null;
        }

        // The video's size, for a clip w x h.
        public static (int W, int H) VideoSize(int w, int h, ExportSize size)
        {
            if (size == ExportSize.Double || size == ExportSize.Discord)
                return (w * 2, h * 2);
            int vh = size == ExportSize.P1080 ? 1080 : size == ExportSize.P1440 ? 1440 : 2160;
            return ((vh * w / h + 1) & ~1, vh);     // (an even width, as H.264's 4:2:0 needs)
        }

        // ffmpeg's scale filter's flags, for each ExportScaling.
        public static readonly string[] ScaleFlags = { "neighbor", "bilinear", "bicubic", "lanczos", "spline" };

        // ---- Discord

        // Discord's upload limit without Nitro (20 MB since August 2026): a
        // Discord export is made to fit under it. Its video is twice the
        // clip's size whatever its length: at the same bitrate, half the size
        // looked no better on real clips (and worse, mostly: H.264's 4:2:0
        // keeps a colour for each 2 x 2 pixels, so only at twice the size are
        // the 3DS's own pixels' colours kept).
        public const long DiscordLimit = 20_000_000;

        // A Discord export's video bitrate at most (kbit/s), for short clips:
        // about what an export at twice the size uses on a busy clip (8 to 14
        // on Mario Kart 7), without filling 20 MB for a few seconds.
        public const int DiscordMaxVideo = 8000;

        // How many times an export goes through the frames: twice for Discord
        // (x264's two passes, which aim at a size), once otherwise.
        public static int Passes(ExportSize size) => size == ExportSize.Discord ? 2 : 1;

        // The bitrates (kbit/s) of a Discord export of 'frames' frames at 'fps'
        // fitting in 'limit' bytes: the file aims at 95% of it (the rest is
        // room for x264 and the AAC encoder missing a little: on real clips,
        // 0.2 to 1.6% over), less the MP4's index, about 1.1 kB a second
        // (each frame's and sound packet's size and place: 661 kB for 10
        // minutes), which leaves the video and sound's bitrates. The sound
        // gets 128 kbit/s, or less for long clips (down to 64), and the video
        // the rest, up to DiscordMaxVideo. Throws if the clip is too long for
        // a watchable video (only with a smaller limit: 10 minutes, the
        // longest a clip can be, still gets about 180 kbit/s).
        public static (int Video, int Audio) DiscordBitrates(int frames, int fps, bool audio, long limit)
        {
            double seconds = Math.Max(1, frames) / (double)fps, total = (limit * 0.95 / seconds - 1200) * 8 / 1000;
            int sound = audio ? (int)Math.Max(64, Math.Min(128, Math.Round(total / 10 / 16) * 16)) : 0;
            int video = (int)Math.Min(DiscordMaxVideo, total - sound);
            if (video < 50)
                throw new Exception($"it's too long to fit in {limit / 1e6:0.##} MB");
            return (video, sound);
        }

        static readonly Dictionary<string, bool> gpuChecked = new Dictionary<string, bool>();

        // Can ffmpeg at 'ffmpeg' encode with the GPU here (an NVIDIA card, with
        // its driver)? It's tried once, on a tiny video: the first time takes a
        // second or so.
        public static bool GpuAvailable(string ffmpeg)
        {
            lock (gpuChecked)
            {
                if (gpuChecked.TryGetValue(ffmpeg, out bool known))
                    return known;
                bool ok = false;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x144:r=30 " +
                                                                        "-frames:v 1 -c:v h264_nvenc -f null -")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.StandardError.ReadToEndAsync();
                        ok = p.WaitForExit(15000) && p.ExitCode == 0;
                        Stop(p);
                    }
                }
                catch (Exception e) when (e is Win32Exception || e is InvalidOperationException)
                {
                }
                gpuChecked[ffmpeg] = ok;
                return ok;
            }
        }

        // RGB565's 5- and 6-bit values as 8-bit ones.
        static readonly byte[] Five = new byte[32], Six = new byte[64];

        static Converter()
        {
            for (int i = 0; i < 32; i++)
                Five[i] = (byte)(i << 3 | i >> 2);
            for (int i = 0; i < 64; i++)
                Six[i] = (byte)(i << 2 | i >> 4);
        }

        // 'pixels' (w x h, RGB565) at 'k' times the size into 'frame' (BGR, 3
        // bytes a pixel, w * k * 3 bytes a row): each pixel becomes k x k.
        public static void Scale(ushort[] pixels, int w, int h, int k, byte[] frame)
        {
            int stride = w * k * 3;
            for (int y = 0; y < h; y++)
            {
                int row = y * k * stride, o = row;
                for (int x = 0, s = y * w; x < w; x++, s++)
                {
                    ushort p = pixels[s];
                    byte b = Five[p & 31], g = Six[(p >> 5) & 63], r = Five[p >> 11];
                    for (int i = 0; i < k; i++)
                    {
                        frame[o] = b;
                        frame[o + 1] = g;
                        frame[o + 2] = r;
                        o += 3;
                    }
                }
                for (int i = 1; i < k; i++)
                    Buffer.BlockCopy(frame, row, frame, row + i * stride, stride);
            }
        }

        // Exports 'clip' as the MP4 'mp4', replacing it if it exists, with ffmpeg
        // at 'ffmpeg', with the input display where 'inputs' says (if it isn't
        // null, and the clip has inputs). 'progress' is told how many frames are
        // done, now and then. Throws OperationCanceledException if 'cancel' stops
        // it, or another exception saying what went wrong; either way, an MP4
        // that was there is left as it was, and nothing else is left behind.
        public static void ToMp4(FlbClip clip, string mp4, InputLayout? inputs, ExportSize size, ExportScaling scaling,
                                 ExportEncoder encoder, string ffmpeg, Action<int> progress, CancellationToken cancel) =>
            ToMp4(clip, 0, clip.FrameCount, mp4, inputs, size, scaling, encoder, ffmpeg, progress, cancel);

        // The same, for frames firstFrame .. firstFrame + frameCount - 1 of it
        // (a trimmed part), with their sound. A Discord export goes through the
        // frames twice (x264's two passes: Passes(size)), and 'progress' counts
        // the frames of both, the second pass's after the first's; it's
        // encoded by the CPU whatever 'encoder' says (NVENC can't aim at a
        // size as well), and if it still comes out over the limit (it never
        // did on real clips), the second pass is done again at a lower bitrate.
        public static void ToMp4(FlbClip clip, int firstFrame, int frameCount, string mp4, InputLayout? inputs,
                                 ExportSize size, ExportScaling scaling, ExportEncoder encoder, string ffmpeg,
                                 Action<int> progress, CancellationToken cancel) =>
            ToMp4(clip, firstFrame, frameCount, mp4, inputs, size, scaling, encoder, ffmpeg, progress, cancel, DiscordLimit);

        // The same, a Discord export fitting in 'limit' bytes (for the tests).
        public static void ToMp4(FlbClip clip, int firstFrame, int frameCount, string mp4, InputLayout? inputs,
                                 ExportSize size, ExportScaling scaling, ExportEncoder encoder, string ffmpeg,
                                 Action<int> progress, CancellationToken cancel, long limit)
        {
            (int w, int h) = VideoSize(clip.Width, clip.Height, size);
            bool here = (size == ExportSize.Double || size == ExportSize.Discord) && scaling == ExportScaling.Nearest;     // (each pixel 2 x 2, here)
            InputOverlay overlay = inputs.HasValue && clip.Inputs != null ? new InputOverlay(w, h, inputs.Value) : null;
            string part = mp4 + ".part";     // the MP4, until it's finished
            string wav = null, stats = null;
            bool done = false;
            Throttle(CurrentProcess, false);
            try
            {
                string sound = "";
                if (clip.HasAudio)
                {
                    wav = Path.Combine(Path.GetTempPath(), "FlashbackStudio-" + Guid.NewGuid().ToString("N") + ".wav");
                    clip.WriteWav(wav, firstFrame, frameCount);
                    sound = $"-i \"{wav}\" -map 0:v -map 1:a ";
                }
                // The frames through ffmpeg, with 'output' after their input on
                // its command line, 'before' frames already done.
                void Pass(string output, int before) =>
                    Encode(clip, firstFrame, frameCount, w, h, here, scaling, overlay, ffmpeg, InputArgs(w, h, clip.Fps) + output,
                           n => progress(before + n), cancel);

                if (size == ExportSize.Discord)
                {
                    (int video, int audio) = DiscordBitrates(frameCount, clip.Fps, clip.HasAudio, limit);
                    // (x264 keeps what the first pass learnt in files named after this.)
                    stats = Path.Combine(Path.GetTempPath(), "FlashbackStudio-" + Guid.NewGuid().ToString("N") + "-x264");
                    // ("slow": on a busy clip at 500 kbit/s, 0.3 dB better than the
                    // default, in 1.7 times the time; "slower" was 1 dB better, in 5.)
                    string x264 = $"-c:v libx264 -preset slow -pix_fmt yuv420p -passlogfile \"{stats}\" ";
                    Pass(x264 + $"-b:v {video}k -pass 1 -an -f null -", 0);
                    for (int tries = 1; ; tries++)
                    {
                        Pass(sound + x264 + $"-b:v {video}k -pass 2 " + (audio > 0 ? $"-c:a aac -b:a {audio}k -ar 48000 " : "") +
                             $"-movflags +faststart -f mp4 \"{part}\"", frameCount);
                        long got = new FileInfo(part).Length;
                        if (got <= limit)
                            break;
                        if (tries == 3)
                            throw new Exception($"it came out at {got / 1e6:0.00} MB, more than the {limit / 1e6:0.##} MB it should fit in");
                        video = (int)(video * 0.95 * limit / got);
                    }
                }
                else
                {
                    StringBuilder args = new StringBuilder(sound);
                    if (encoder == ExportEncoder.Gpu)
                    {
                        // The same quality as x264's below (on a real clip scaled to
                        // 4K, PSNR 54.8 dB for both), encoded in about a third of the
                        // time (6.6 s instead of 17.7), in files a third to a half
                        // bigger. (A whole export gains less: the frames' way through
                        // the scaler and here takes its time too.)
                        args.Append("-c:v h264_nvenc -preset p4 -rc constqp -qp 12 -pix_fmt yuv420p ");
                    }
                    else
                    {
                        args.Append("-c:v libx264 -crf 12 -pix_fmt yuv420p ");
                        if (size != ExportSize.Double)
                            args.Append("-preset faster ");     // (at 1080p, about 3 times quicker than the default, for much the same file)
                    }
                    if (clip.HasAudio)
                        args.Append("-c:a aac -b:a 192k -ar 48000 ");
                    args.Append($"-f mp4 \"{part}\"");
                    Pass(args.ToString(), 0);
                }

                if (File.Exists(mp4))
                    File.Delete(mp4);
                File.Move(part, mp4);
                done = true;
            }
            finally
            {
                if (wav != null)
                    TryDelete(wav);
                if (stats != null)
                    foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(stats) + "*"))
                        TryDelete(file);
                if (!done)
                    TryDelete(part);
                Throttle(CurrentProcess, true);
            }
        }

        // Sends frames firstFrame .. firstFrame + frameCount - 1 of 'clip', w x h
        // (scaled here if 'here', each pixel 2 x 2, or else by a second ffmpeg,
        // with 'scaling'), with the input display drawn over them by 'overlay'
        // (if it isn't null), to an ffmpeg started with 'args', and waits for it
        // to finish. 'progress' is told how many frames are done, now and then.
        static void Encode(FlbClip clip, int firstFrame, int frameCount, int w, int h, bool here, ExportScaling scaling,
                           InputOverlay overlay, string ffmpeg, string args, Action<int> progress, CancellationToken cancel)
        {
            Process p = null, scaler = null;
            List<string> errors = new List<string>(), scalerErrors = new List<string>();
            Thread feeder = null;
            Exception fed = null;           // what stopped the feeder early, if anything
            try
            {
                p = StartFfmpeg(ffmpeg, args, false, errors);

                // The frames before the input display: scaled here, or sent to the
                // scaler by the feeder thread (with how many times each is shown)
                // and read back from it.
                IEnumerator<(ushort[] Pixels, int Copies)> frames = null;
                BlockingCollection<int> sent = null;
                Stream scaled = null;
                if (here)
                    frames = clip.Frames(firstFrame, frameCount).GetEnumerator();
                else
                {
                    scaler = StartFfmpeg(ffmpeg, InputArgs(clip.Width, clip.Height, clip.Fps) +
                                         $"-vf scale={w}:{h}:flags={ScaleFlags[(int)scaling]} -f rawvideo -pix_fmt bgr24 -",
                                         true, scalerErrors);
                    scaled = scaler.StandardOutput.BaseStream;
                    sent = new BlockingCollection<int>();
                    Stream toScaler = scaler.StandardInput.BaseStream;
                    feeder = new Thread(() => fed = Feed(clip, firstFrame, frameCount, toScaler, sent))
                    {
                        IsBackground = true,
                        Name = "frames to scale",
                    };
                    feeder.Start();
                }

                Stream stdin = p.StandardInput.BaseStream;
                byte[] clean = new byte[w * h * 3], frame = overlay != null ? new byte[clean.Length] : clean;
                int written = 0;
                bool stopped = false;   // ffmpeg stopped reading the frames, or the scaler stopped sending them
                while (!stopped)
                {
                    int copies;
                    if (here)
                    {
                        if (!frames.MoveNext())
                            break;
                        Scale(frames.Current.Pixels, clip.Width, clip.Height, 2, clean);
                        copies = frames.Current.Copies;
                    }
                    else
                    {
                        if (!sent.TryTake(out copies, Timeout.Infinite, cancel))
                            break;      // (all of them sent)
                        if (!ReadAll(scaled, clean))
                        {
                            stopped = true;
                            break;
                        }
                    }
                    for (int c = 0; c < copies && !stopped; c++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (overlay != null)
                        {
                            Buffer.BlockCopy(clean, 0, frame, 0, clean.Length);
                            overlay.Draw(frame, clip.Inputs[firstFrame + written]);
                        }
                        try
                        {
                            stdin.Write(frame, 0, frame.Length);
                            written++;
                        }
                        catch (IOException)
                        {
                            stopped = true;     // (its exit code and messages say why)
                        }
                    }
                    if (stopped)
                        break;
                    progress(written);
                }
                try
                {
                    stdin.Close();
                }
                catch (IOException)
                {
                }

                // Short of frames from the scaler: a damaged frame, or the scaler
                // failed (if it stopped by itself), or else the encoder did.
                if (scaler != null && written != frameCount)
                {
                    bool scalerFailed = scaler.HasExited && scaler.ExitCode != 0;
                    Stop(scaler);
                    feeder.Join();
                    if (fed is InvalidDataException)
                        ExceptionDispatchInfo.Capture(fed).Throw();
                    if (scalerFailed)
                        lock (scalerErrors)
                            throw new Exception("ffmpeg failed: " + Reason(scalerErrors, scaler.ExitCode));
                }

                // Then it finishes the video.
                while (!p.WaitForExit(100))
                    cancel.ThrowIfCancellationRequested();
                p.WaitForExit();    // (and its last messages)
                if (p.ExitCode != 0 || written != frameCount)
                    lock (errors)
                        throw new Exception("ffmpeg failed: " + Reason(errors, p.ExitCode));
            }
            finally
            {
                Stop(p);
                Stop(scaler);
                feeder?.Join();     // (its writes fail once the scaler's gone)
                p?.Dispose();
                scaler?.Dispose();
            }
        }

        // The start of an ffmpeg command line taking raw BGR frames w x h on its input.
        static string InputArgs(int w, int h, int fps)
        {
            StringBuilder args = new StringBuilder("-hide_banner -loglevel error -y ");
            // .NET writes the byte-order mark of the console's input encoding, if
            // it has one (UTF-8's), to ffmpeg's input as soon as it starts it:
            // ffmpeg skips it, so the frames start where they should.
            int bom = Console.InputEncoding.GetPreamble().Length;
            if (bom > 0)
                args.Append("-skip_initial_bytes ").Append(bom).Append(' ');
            args.Append($"-f rawvideo -pixel_format bgr24 -video_size {w}x{h} -framerate {fps} -i - ");
            return args.ToString();
        }

        // Starts ffmpeg with 'args', its input ours to write (and with 'output',
        // its output ours to read), and the messages it writes collected in
        // 'errors'.
        static Process StartFfmpeg(string ffmpeg, string args, bool output, List<string> errors)
        {
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = output,
                RedirectStandardError = true,
            };
            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Win32Exception e)
            {
                throw new Exception("ffmpeg couldn't be started (" + e.Message.TrimEnd('.') + ")");
            }
            p.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    lock (errors)
                        errors.Add(e.Data.Trim());
            };
            p.BeginErrorReadLine();
            Throttle(p.Handle, false);
            return p;
        }

        // Ends ffmpeg 'p' (if it's still running).
        static void Stop(Process p)
        {
            if (p == null)
                return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit();
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is Win32Exception)
            {
            }
        }

        // The feeder thread: sends the clip's stored frames (BGR, at its size) to
        // the scaler's input, after adding how many times each is shown to
        // 'sent', and then closes both. Returns what stopped it early (a damaged
        // frame, or the scaler gone), or null.
        static Exception Feed(FlbClip clip, int firstFrame, int frameCount, Stream toScaler, BlockingCollection<int> sent)
        {
            try
            {
                byte[] frame = new byte[clip.Width * clip.Height * 3];
                foreach ((ushort[] pixels, int copies) in clip.Frames(firstFrame, frameCount))
                {
                    Scale(pixels, clip.Width, clip.Height, 1, frame);
                    sent.Add(copies);
                    toScaler.Write(frame, 0, frame.Length);
                }
                return null;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is ObjectDisposedException)
            {
                return e;
            }
            finally
            {
                sent.CompleteAdding();
                try
                {
                    toScaler.Close();
                }
                catch (IOException)
                {
                }
            }
        }

        // Fills 'buffer' from 'from'; false if it ends first.
        static bool ReadAll(Stream from, byte[] buffer)
        {
            for (int got = 0, n; got < buffer.Length; got += n)
                if ((n = from.Read(buffer, got, buffer.Length - got)) <= 0)
                    return false;
            return true;
        }

        // ffmpeg's last message that says why it failed (its very last can be just
        // "Conversion failed!"), without the part saying which part of it wrote it.
        static string Reason(List<string> messages, int exitCode)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                string message = Regex.Replace(messages[i], @"^\[[^\]]* @ [0-9A-Fa-fx]+\]\s*", "");
                if (message.Length > 0 && message != "Conversion failed!")
                    return message;
            }
            return "exit code " + exitCode;
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        // ---- Exporting at full speed

        // Windows 11 can slow a process down while it isn't the one in front
        // (EcoQoS: on a CPU with efficiency cores, it runs on those, at a low
        // clock), and exporting is work you leave it to do: on a 12th-gen Intel,
        // heavy work in the background ran about 3 times slower. So while an
        // export runs, this process and its ffmpegs ask not to be slowed down;
        // afterwards, Windows decides again.

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState
        {
            public uint Version, ControlMask, StateMask;
        }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        static readonly IntPtr CurrentProcess = new IntPtr(-1);     // (GetCurrentProcess()'s handle)

        // 'allowed' false: 'process' is never slowed down; true: Windows decides,
        // as it does to start with. (Nothing, on Windows before 10.)
        static void Throttle(IntPtr process, bool allowed)
        {
            // ProcessPowerThrottling (4), version 1, its execution speed (1):
            // controlled and off, or not controlled.
            PowerThrottlingState state = new PowerThrottlingState { Version = 1, ControlMask = allowed ? 0u : 1u, StateMask = 0 };
            try
            {
                SetProcessInformation(process, 4, ref state, Marshal.SizeOf(state));
            }
            catch (EntryPointNotFoundException)
            {
                // (Windows 7: no SetProcessInformation)
            }
        }
    }
}
