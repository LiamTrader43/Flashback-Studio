using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace FlashbackStudio
{
    // A clip in the folder, and what's known about it.
    sealed class ClipInfo
    {
        public string Path, Name;
        public string Error;            // why it can't be played, or null
        public int Width, Height, Fps, FrameCount, EntryCount;
        public bool HasAudio, HasInputs, IsCompacted, IsRaw;
        public long FileSize;
        public long CompactSize;        // its size once compacted
        public Bitmap Thumb;            // its first frame, small, once read...
        public bool ThumbFailed;        // ...or if that failed

        public string Mp4 => System.IO.Path.ChangeExtension(Path, ".mp4");
        public int MissedFrames => FrameCount - EntryCount;
    }

    // The clips in a folder (like the 3DS app's sd:/luma/flashback/), and their
    // thumbnails, read on a thread of their own.
    static class Library
    {
        // The clips in 'folder', in the 3DS app's order (see Names.Compare): .flb
        // clips, and .raw clips from older builds (which have a .txt next to
        // them). Leftovers of a compaction that was cut off are cleaned up first.
        public static List<ClipInfo> Scan(string folder)
        {
            List<ClipInfo> clips = new List<ClipInfo>();
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return clips;
            }
            foreach (string f in files)
                FlbCompactor.CleanUpLeftover(f);
            foreach (string f in Directory.GetFiles(folder).OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(Names.Compare)))
            {
                string ext = Path.GetExtension(f);
                if (string.Equals(ext, ".flb", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(ext, ".raw", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(f, ".txt"))))
                    clips.Add(Read(f));
            }
            return clips;
        }

        // What a clip's index says about it.
        public static ClipInfo Read(string path)
        {
            ClipInfo c = new ClipInfo { Path = path, Name = Path.GetFileName(path) };
            try
            {
                using (FlbClip clip = FlbClip.Open(path))
                {
                    c.Width = clip.Width;
                    c.Height = clip.Height;
                    c.Fps = clip.Fps;
                    c.FrameCount = clip.FrameCount;
                    c.EntryCount = clip.EntryCount;
                    c.HasAudio = clip.HasAudio;
                    c.HasInputs = clip.Inputs != null;
                    c.IsCompacted = clip.IsCompacted;
                    c.IsRaw = clip.IsRaw;
                    c.FileSize = clip.FileSize;
                    c.CompactSize = clip.IsRaw ? 0 : FlbCompactor.CompactSize(clip);
                }
            }
            catch (InvalidDataException e)
            {
                c.Error = e.Message;
                try
                {
                    c.FileSize = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }
            return c;
        }

        // A w x h frame (RGB565) shrunk to tw x th, each pixel the average of the
        // ones it covers (the 3DS app's shrinkFrame).
        public static Bitmap Shrink(ushort[] src, int w, int h, int tw, int th)
        {
            byte[] bgr = new byte[tw * th * 3];
            for (int ty = 0; ty < th; ty++)
            {
                int y0 = ty * h / th, y1 = Math.Max(y0 + 1, (ty + 1) * h / th);
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = tx * w / tw, x1 = Math.Max(x0 + 1, (tx + 1) * w / tw);
                    int r = 0, g = 0, b = 0, n = (y1 - y0) * (x1 - x0);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            ushort p = src[y * w + x];
                            r += p >> 11;
                            g += (p >> 5) & 63;
                            b += p & 31;
                        }
                    int o = (ty * tw + tx) * 3;
                    bgr[o] = (byte)(b * 255 / (31 * n));
                    bgr[o + 1] = (byte)(g * 255 / (63 * n));
                    bgr[o + 2] = (byte)(r * 255 / (31 * n));
                }
            }
            return ToBitmap(bgr, tw, th);
        }

        public static Bitmap ToBitmap(byte[] bgr, int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgr, y * w * 3, d.Scan0 + y * d.Stride, w * 3);
            bmp.UnlockBits(d);
            return bmp;
        }
    }

    // Reads clips' thumbnails on a thread of its own, the ones asked for most
    // recently first, and hands each over (on that thread) when it's ready.
    sealed class ThumbLoader : IDisposable
    {
        readonly Action<ClipInfo, Bitmap> ready;
        readonly List<ClipInfo> wanted = new List<ClipInfo>();
        readonly Thread thread;
        bool stopping;
        public int Width, Height;       // the thumbnails' size

        public ThumbLoader(Action<ClipInfo, Bitmap> ready)
        {
            this.ready = ready;
            thread = new Thread(Run) { IsBackground = true, Name = "thumbnails" };
            thread.Start();
        }

        // Replaces what's wanted: these clips, first first.
        public void Want(IEnumerable<ClipInfo> clips)
        {
            lock (wanted)
            {
                wanted.Clear();
                wanted.AddRange(clips.Where(c => c.Thumb == null && !c.ThumbFailed && c.Error == null));
                Monitor.Pulse(wanted);
            }
        }

        void Run()
        {
            ushort[] frame = new ushort[400 * 240];
            while (true)
            {
                ClipInfo c;
                int tw, th;
                lock (wanted)
                {
                    while (!stopping && wanted.Count == 0)
                        Monitor.Wait(wanted);
                    if (stopping)
                        return;
                    c = wanted[0];
                    wanted.RemoveAt(0);
                    tw = Width;
                    th = Height;
                }
                Bitmap thumb = null;
                try
                {
                    using (FlbClip clip = FlbClip.Open(c.Path))
                    {
                        clip.DecodeEntry(0, frame);
                        thumb = Library.Shrink(frame, clip.Width, clip.Height, tw, th);
                    }
                }
                catch (InvalidDataException)
                {
                }
                ready(c, thumb);
            }
        }

        public void Dispose()
        {
            lock (wanted)
            {
                stopping = true;
                Monitor.Pulse(wanted);
            }
        }
    }
}
