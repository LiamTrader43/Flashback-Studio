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
    // tall (keeping the 3DS screen's shape)...
    enum ExportSize { Double, P1080, P1440, P2160 }

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
            if (size == ExportSize.Double)
                return (w * 2, h * 2);
            int vh = size == ExportSize.P1080 ? 1080 : size == ExportSize.P1440 ? 1440 : 2160;
            return ((vh * w / h + 1) & ~1, vh);     // (an even width, as H.264's 4:2:0 needs)
        }

        // ffmpeg's scale filter's flags, for each ExportScaling.
        public static readonly string[] ScaleFlags = { "neighbor", "bilinear", "bicubic", "lanczos", "spline" };

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
        // at 'ffmpeg'. 'progress' is told how many frames are done, now and then.
        // Throws OperationCanceledException if 'cancel' stops it, or another
        // exception saying what went wrong; either way, an MP4 that was there is
        // left as it was, and nothing else is left behind.
        public static void ToMp4(FlbClip clip, string mp4, bool showInputs, ExportSize size, ExportScaling scaling,
                                 ExportEncoder encoder, string ffmpeg, Action<int> progress, CancellationToken cancel) =>
            ToMp4(clip, 0, clip.FrameCount, mp4, showInputs, size, scaling, encoder, ffmpeg, progress, cancel);

        // The same, for frames firstFrame .. firstFrame + frameCount - 1 of it
        // (a trimmed part), with their sound.
        public static void ToMp4(FlbClip clip, int firstFrame, int frameCount, string mp4, bool showInputs,
                                 ExportSize size, ExportScaling scaling, ExportEncoder encoder, string ffmpeg,
                                 Action<int> progress, CancellationToken cancel)
        {
            (int w, int h) = VideoSize(clip.Width, clip.Height, size);
            bool here = size == ExportSize.Double && scaling == ExportScaling.Nearest;     // (each pixel 2 x 2, here)
            InputOverlay overlay = showInputs && clip.Inputs != null ? new InputOverlay(w, h) : null;
            string part = mp4 + ".part";     // the MP4, until it's finished
            string wav = null;
            Process p = null, scaler = null;
            List<string> errors = new List<string>(), scalerErrors = new List<string>();
            Thread feeder = null;
            Exception fed = null;           // what stopped the feeder early, if anything
            bool done = false;
            Throttle(CurrentProcess, false);
            try
            {
                StringBuilder args = new StringBuilder(InputArgs(w, h, clip.Fps));
                if (clip.HasAudio)
                {
                    wav = Path.Combine(Path.GetTempPath(), "FlashbackStudio-" + Guid.NewGuid().ToString("N") + ".wav");
                    clip.WriteWav(wav, firstFrame, frameCount);
                    args.Append($"-i \"{wav}\" -map 0:v -map 1:a ");
                }
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
                p = StartFfmpeg(ffmpeg, args.ToString(), false, errors);

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

                if (File.Exists(mp4))
                    File.Delete(mp4);
                File.Move(part, mp4);
                done = true;
            }
            finally
            {
                Stop(p);
                Stop(scaler);
                feeder?.Join();     // (its writes fail once the scaler's gone)
                p?.Dispose();
                scaler?.Dispose();
                if (wav != null)
                    TryDelete(wav);
                if (!done)
                    TryDelete(part);
                Throttle(CurrentProcess, true);
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
