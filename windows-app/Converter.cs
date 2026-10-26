using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FlashbackConverter
{
    // Turns clips into MP4 videos with ffmpeg: H.264 at twice the clip's size,
    // with sharp, unblurred pixels (each one becomes 2 x 2), at the frame rate it
    // was recorded at, with its sound as an AAC track, and the input display
    // drawn over it if asked.
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

        // RGB565's 5- and 6-bit values as 8-bit ones.
        static readonly byte[] Five = new byte[32], Six = new byte[64];

        static Converter()
        {
            for (int i = 0; i < 32; i++)
                Five[i] = (byte)(i << 3 | i >> 2);
            for (int i = 0; i < 64; i++)
                Six[i] = (byte)(i << 2 | i >> 4);
        }

        // 'pixels' (w x h, RGB565) at twice the size into 'frame' (BGR).
        static void Double(ushort[] pixels, int w, int h, byte[] frame)
        {
            int stride = w * 6;
            for (int y = 0; y < h; y++)
            {
                int o = y * 2 * stride;
                for (int x = 0, s = y * w; x < w; x++, s++)
                {
                    ushort p = pixels[s];
                    byte b = Five[p & 31], g = Six[(p >> 5) & 63], r = Five[p >> 11];
                    frame[o] = b;
                    frame[o + 1] = g;
                    frame[o + 2] = r;
                    frame[o + 3] = b;
                    frame[o + 4] = g;
                    frame[o + 5] = r;
                    o += 6;
                }
                Buffer.BlockCopy(frame, y * 2 * stride, frame, (y * 2 + 1) * stride, stride);
            }
        }

        // Converts 'clip' into the MP4 'mp4', replacing it if it exists, with
        // ffmpeg at 'ffmpeg'. 'progress' is told how many frames are done, now and
        // then. Throws OperationCanceledException if 'cancel' stops it, or another
        // exception saying what went wrong; either way, an MP4 that was there is
        // left as it was, and nothing else is left behind.
        public static void ToMp4(FlbClip clip, string mp4, bool showInputs, string ffmpeg, Action<int> progress,
                                 CancellationToken cancel)
        {
            int w = clip.Width * 2, h = clip.Height * 2;
            InputOverlay overlay = showInputs && clip.Inputs != null ? new InputOverlay(w, h) : null;
            string part = mp4 + ".part";     // the MP4, until it's finished
            string wav = null;
            Process p = null;
            bool done = false;
            try
            {
                StringBuilder args = new StringBuilder("-hide_banner -loglevel error -y ");
                // .NET writes the byte-order mark of the console's input encoding, if
                // it has one (UTF-8's), to ffmpeg's input as soon as it starts it:
                // ffmpeg skips it, so the frames start where they should.
                int bom = Console.InputEncoding.GetPreamble().Length;
                if (bom > 0)
                    args.Append("-skip_initial_bytes ").Append(bom).Append(' ');
                args.Append($"-f rawvideo -pixel_format bgr24 -video_size {w}x{h} -framerate {clip.Fps} -i - ");
                if (clip.HasAudio)
                {
                    wav = Path.Combine(Path.GetTempPath(), "FlashbackConverter-" + Guid.NewGuid().ToString("N") + ".wav");
                    clip.WriteWav(wav);
                    args.Append($"-i \"{wav}\" -map 0:v -map 1:a ");
                }
                args.Append("-c:v libx264 -crf 12 -pix_fmt yuv420p ");
                if (clip.HasAudio)
                    args.Append("-c:a aac -b:a 192k -ar 48000 ");
                args.Append($"-f mp4 \"{part}\"");

                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                };
                List<string> errors = new List<string>();
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

                Stream stdin = p.StandardInput.BaseStream;
                byte[] clean = new byte[w * h * 3], frame = overlay != null ? new byte[clean.Length] : clean;
                int written = 0;
                bool stopped = false;   // ffmpeg stopped reading the frames
                foreach ((ushort[] pixels, int copies) in clip.Frames())
                {
                    Double(pixels, clip.Width, clip.Height, clean);
                    for (int c = 0; c < copies && !stopped; c++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (overlay != null)
                        {
                            Buffer.BlockCopy(clean, 0, frame, 0, clean.Length);
                            overlay.Draw(frame, clip.Inputs[written]);
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

                // Then it finishes the video.
                while (!p.WaitForExit(100))
                    cancel.ThrowIfCancellationRequested();
                p.WaitForExit();    // (and its last messages)
                if (p.ExitCode != 0 || written != clip.FrameCount)
                    lock (errors)
                        throw new Exception("ffmpeg failed: " + Reason(errors, p.ExitCode));

                if (File.Exists(mp4))
                    File.Delete(mp4);
                File.Move(part, mp4);
                done = true;
            }
            finally
            {
                if (p != null)
                {
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
                    p.Dispose();
                }
                if (wav != null)
                    TryDelete(wav);
                if (!done)
                    TryDelete(part);
            }
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
    }
}
