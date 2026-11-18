using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // The "top screen": a clip's frame, at the biggest whole number of times the
    // 3DS's 400 x 240 that fits (so its pixels stay sharp and even), in the
    // middle of black, with the input display over it if asked, where its
    // InputsAt says. While it's Editing (the player's settings are open), the
    // input display has a frame around it, and can be dragged to move it, or by
    // a corner to resize it. A clip's bottom screen, if it's given one, goes
    // under the frame, centred, as on a 3DS (and the scale is then the biggest
    // that fits both).
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
        ushort[] bottom;            // the bottom screen's, under it (null: none)
        int bottomW, bottomH;
        FlbInput? input;            // its inputs, if the display is to be drawn
        byte[] bgr;                 // it, drawn at 'scale' (bgrW x bgrH), with the bottom screen's under it (bgrW x allH in all)
        int scale, bgrW, bgrH, allH;
        InputOverlay overlay;
        InputLayout layout = InputLayout.Default;
        bool editing;

        // What's being dragged: one of the display's corners (0 to 3, as
        // InputLayout.Resized numbers them), all of it, or nothing...
        const int Nothing = -1, All = 4;
        int grab = Nothing;
        Point grabAt;               // ...where the mouse was when it was grabbed...
        InputLayout grabbed;        // ...and where the display was

        public TopScreen()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        // A click on the picture (not on the input display while it's Editing).
        public event Action Clicked;

        // After the input display was dragged somewhere else, or resized (to InputsAt).
        public event Action InputsMoved;

        public InputLayout InputsAt
        {
            get => layout;
            set
            {
                layout = value.Clamped();
                if (pixels != null)
                    Render();
                Invalidate();
            }
        }

        public bool Editing
        {
            get => editing;
            set
            {
                if (value == editing)
                    return;
                editing = value;
                if (!editing)
                {
                    EndDrag();
                    Cursor = Cursors.Default;
                }
                Invalidate();
            }
        }

        int S(int px) => px * DeviceDpi / 96;

        // How many times 400 x 240 it shows the frame (400 x 480, with a bottom screen under it).
        int ScreenScale => Math.Max(1, Math.Min(ClientSize.Width / 400, ClientSize.Height / (bottom != null ? 480 : 240)));

        // Shows a frame ('pixels', w x h, RGB565; copied), with the input display
        // for 'input' if it's given, and the bottom screen's frame under it
        // ('bottom', bw x bh) if that's given; or nothing, if 'pixels' is null.
        // Returns how long drawing it took, in milliseconds.
        public double Show(ushort[] pixels, int w, int h, FlbInput? input, ushort[] bottom = null, int bw = 0, int bh = 0)
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
            if (pixels == null || bottom == null)
                this.bottom = null;
            else
            {
                if (this.bottom == null || this.bottom.Length != bottom.Length)
                    this.bottom = new ushort[bottom.Length];
                Array.Copy(bottom, this.bottom, bottom.Length);
            }
            clipW = w;
            clipH = h;
            bottomW = bw;
            bottomH = bh;
            this.input = input;
            Render();
            if (!Handles)
                EndDrag();
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
            allH = bgrH + (bottom != null ? bottomH * k : 0);
            if (bgr == null || bgr.Length != bgrW * allH * 3)
                bgr = new byte[bgrW * allH * 3];    // (black, where nothing's drawn: beside the bottom screen)
            Converter.Scale(pixels, clipW, clipH, k, bgr);
            if (bottom != null)
                Converter.Scale(bottom, bottomW, bottomH, k, bgr, bgrW * 3, (clipW - bottomW) / 2 * k, bgrH);
            if (input.HasValue)
            {
                // (Made again only for a new size: moving it, or another colour, is quick.)
                if (overlay == null || !overlay.Fits(bgrW, bgrH, layout))
                    overlay = new InputOverlay(bgrW, bgrH, layout);
                else
                    overlay.Place(layout);
                overlay.Draw(bgr, input.Value);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (pixels != null && ScreenScale != scale)
                Render();
        }

        // ---- Dragging the input display

        // Whether the input display is shown with its frame (and can be dragged).
        bool Handles => editing && bgr != null && input.HasValue && overlay != null;

        // Where the input display is, here.
        Rectangle Box => new Rectangle((ClientSize.Width - bgrW) / 2 + overlay.X, (ClientSize.Height - allH) / 2 + overlay.Y,
                                       overlay.BoxWidth, overlay.BoxHeight);

        static Point Corner(Rectangle box, int corner) =>
            new Point((corner & 1) != 0 ? box.Right : box.X - 1, (corner & 2) != 0 ? box.Bottom : box.Y - 1);

        // What's at 'at': one of the input display's corners (near enough), the
        // rest of it, or nothing.
        int Hit(Point at)
        {
            if (!Handles)
                return Nothing;
            Rectangle box = Box;
            int reach = S(7);
            for (int c = 0; c < 4; c++)
            {
                Point p = Corner(box, c);
                if (Math.Abs(at.X - p.X) <= reach && Math.Abs(at.Y - p.Y) <= reach)
                    return c;
            }
            return box.Contains(at) ? All : Nothing;
        }

        static Cursor CursorFor(int hit) =>
            hit == All ? Cursors.SizeAll : hit == 0 || hit == 3 ? Cursors.SizeNWSE : hit == 1 || hit == 2 ? Cursors.SizeNESW : Cursors.Default;

        void EndDrag()
        {
            if (grab == Nothing)
                return;
            grab = Nothing;
            InputsMoved?.Invoke();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            int hit = Hit(e.Location);
            if (hit != Nothing)
            {
                grab = hit;
                grabAt = e.Location;
                grabbed = layout;
            }
            else
                Clicked?.Invoke();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (grab == Nothing)
            {
                Cursor = CursorFor(Hit(e.Location));
                return;
            }
            // (How far the mouse went, in the top screen's pixels.)
            float dx = (e.X - grabAt.X) * 240f / bgrH, dy = (e.Y - grabAt.Y) * 240f / bgrH;
            InputLayout moved = grab == All ? grabbed.Moved(dx, dy) : grabbed.Resized(grab, dx, dy);
            if (!moved.Equals(layout))
            {
                layout = moved;
                Render();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                EndDrag();
                Cursor = CursorFor(Hit(e.Location));
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture)
                EndDrag();      // (the mouse was taken away mid-drag: it stays where it got to)
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (bgr == null)
                return;
            int x = (ClientSize.Width - bgrW) / 2, y = (ClientSize.Height - allH) / 2;
            BitmapInfoHeader info = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf(typeof(BitmapInfoHeader)),
                Width = bgrW,
                Height = -allH,     // (top row first)
                Planes = 1,
                BitCount = 24,
            };
            IntPtr dc = e.Graphics.GetHdc();
            try
            {
                SetDIBitsToDevice(dc, x, y, bgrW, allH, 0, 0, 0, allH, bgr, ref info, 0);
            }
            finally
            {
                e.Graphics.ReleaseHdc(dc);
            }

            if (Handles)
            {
                // A dashed frame around the input display (white on black, to show
                // on any picture), with a square at each corner to resize it by.
                Rectangle box = Box, frame = new Rectangle(box.X - 1, box.Y - 1, box.Width + 1, box.Height + 1);
                using (Pen black = new Pen(Color.Black))
                using (Pen white = new Pen(Color.White) { DashStyle = DashStyle.Dash })
                using (Brush fill = new SolidBrush(Color.White))
                {
                    e.Graphics.DrawRectangle(black, frame);
                    e.Graphics.DrawRectangle(white, frame);
                    int r = S(4);
                    for (int c = 0; c < 4; c++)
                    {
                        Point p = Corner(box, c);
                        Rectangle handle = new Rectangle(p.X - r, p.Y - r, 2 * r, 2 * r);
                        e.Graphics.FillRectangle(fill, handle);
                        e.Graphics.DrawRectangle(black, handle);
                    }
                }
            }
        }
    }
}
