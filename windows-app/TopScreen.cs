using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // The "top screen": a clip's frame, at the biggest whole number of times the
    // 3DS's 400 x 240 that fits (so its pixels stay sharp and even), in the
    // middle of black, with the input display over it if asked.
    sealed class TopScreen : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("gdi32.dll")]
        static extern int SetDIBitsToDevice(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int start, int lines,
                                            byte[] bits, ref BitmapInfoHeader info, int usage);

        ushort[] pixels;            // the frame shown (null: none)
        int clipW, clipH;
        FlbInput? input;            // its inputs, if the display is to be drawn
        byte[] bgr;                 // it, drawn at 'scale'
        int scale, bgrW, bgrH;
        InputOverlay overlay;

        public TopScreen()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        public event Action Clicked;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                Clicked?.Invoke();
        }

        // How many times 400 x 240 it shows the frame.
        int ScreenScale => Math.Max(1, Math.Min(ClientSize.Width / 400, ClientSize.Height / 240));

        // Shows a frame ('pixels', w x h, RGB565; copied), with the input display
        // for 'input' if it's given; or nothing, if 'pixels' is null. Returns how
        // long drawing it took, in milliseconds.
        public double Show(ushort[] pixels, int w, int h, FlbInput? input)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            if (pixels == null)
                this.pixels = null;
            else
            {
                if (this.pixels == null || this.pixels.Length != pixels.Length)
                    this.pixels = new ushort[pixels.Length];
                Array.Copy(pixels, this.pixels, pixels.Length);
            }
            clipW = w;
            clipH = h;
            this.input = input;
            Render();
            Invalidate();
            return sw.Elapsed.TotalMilliseconds;
        }

        // Draws the frame at the scale that fits now.
        void Render()
        {
            if (pixels == null)
            {
                bgr = null;
                return;
            }
            scale = ScreenScale;
            int k = 400 * scale / clipW;    // (a half resolution clip at twice that)
            bgrW = clipW * k;
            bgrH = clipH * k;
            if (bgr == null || bgr.Length != bgrW * bgrH * 3)
                bgr = new byte[bgrW * bgrH * 3];
            Converter.Scale(pixels, clipW, clipH, k, bgr);
            if (input.HasValue)
            {
                if (overlay == null || overlay.Width != bgrW || overlay.Height != bgrH)
                    overlay = new InputOverlay(bgrW, bgrH);
                overlay.Draw(bgr, input.Value);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (pixels != null && ScreenScale != scale)
                Render();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (bgr == null)
                return;
            int x = (ClientSize.Width - bgrW) / 2, y = (ClientSize.Height - bgrH) / 2;
            BitmapInfoHeader info = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf(typeof(BitmapInfoHeader)),
                Width = bgrW,
                Height = -bgrH,     // (top row first)
                Planes = 1,
                BitCount = 24,
            };
            IntPtr dc = e.Graphics.GetHdc();
            try
            {
                SetDIBitsToDevice(dc, x, y, bgrW, bgrH, 0, 0, 0, bgrH, bgr, ref info, 0);
            }
            finally
            {
                e.Graphics.ReleaseHdc(dc);
            }
        }
    }
}
