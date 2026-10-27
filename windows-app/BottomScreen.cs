using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // The colours of the 3DS app's bottom screen (main.c's C_* and COLOR_*).
    enum Style { Normal, Title, Selected, Dim, Key, Warn, Info }

    // How a button looks: grey, or red (the screen's main button, or one that's on).
    enum Look { Normal, Red }

    // The pictures on the player's buttons.
    enum Glyph { None, Play, Pause, FrameBack, FrameOn, SecondBack, SecondOn }

    static class Theme
    {
        public static readonly Color Red = Color.FromArgb(204, 26, 31);        // the logo's red: the title bar, and what's played
        public static readonly Color Rest = Color.FromArgb(60, 60, 60);        // the bar after the playhead
        public static readonly Color Head = Color.White;                       // the playhead, and the selected tile's card
        public static readonly Color Missed = Color.FromArgb(255, 60, 60);     // marks where frames were missed
        public static readonly Color Tile = Color.FromArgb(60, 60, 60);        // a thumbnail's frame
        public static readonly Color Loading = Color.FromArgb(25, 25, 25);     // a thumbnail not read yet...
        public static readonly Color Damaged = Color.FromArgb(110, 20, 20);    // ...or that can't be
        public static readonly Color Cut = Color.FromArgb(20, 20, 20);         // trimming: the bar outside the selection...
        public static readonly Color Mark = Color.FromArgb(255, 200, 0);       // ...and its start and end
        public static readonly Color Button = Color.FromArgb(44, 44, 44);      // a button...
        public static readonly Color ButtonHover = Color.FromArgb(68, 68, 68); // ...under the mouse...
        public static readonly Color ButtonOff = Color.FromArgb(26, 26, 26);   // ...that can't be pressed now...
        public static readonly Color RedHover = Color.FromArgb(232, 52, 58);   // ...and a red one (the main one, or one that's on) under the mouse
        public static readonly Color SwitchOff = Color.FromArgb(85, 85, 85);

        public static Color Fore(Style s)
        {
            switch (s)
            {
                case Style.Title: return Color.White;
                case Style.Selected: return Color.Black;
                case Style.Dim: return Color.FromArgb(125, 125, 125);
                case Style.Key: return Color.FromArgb(255, 220, 40);
                case Style.Warn: return Color.FromArgb(255, 80, 80);
                case Style.Info: return Color.White;
                default: return Color.FromArgb(205, 205, 205);
            }
        }

        public static Color? Back(Style s) => s == Style.Title ? Red : s == Style.Selected ? Color.FromArgb(225, 225, 225) : (Color?)null;
        public static bool Bold(Style s) => s == Style.Title || s == Style.Key || s == Style.Warn || s == Style.Info;
    }

    // A dropdown's list (a ContextMenuStrip), in the buttons' colours: grey,
    // lighter under the mouse, and the chosen one (its item Checked) red.
    sealed class DropdownRenderer : ToolStripProfessionalRenderer
    {
        public DropdownRenderer() : base(new Colors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            bool chosen = e.Item is ToolStripMenuItem item && item.Checked, hover = e.Item.Selected && e.Item.Enabled;
            Color face = chosen ? (hover ? Theme.RedHover : Theme.Red) : hover ? Theme.ButtonHover : Theme.Button;
            using (Brush b = new SolidBrush(face))
                e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(120, 120, 120);    // (one that can't be chosen: grey)
            base.OnRenderItemText(e);
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Button;
            public override Color ImageMarginGradientBegin => Theme.Button;
            public override Color ImageMarginGradientMiddle => Theme.Button;
            public override Color ImageMarginGradientEnd => Theme.Button;
            public override Color MenuBorder => Theme.ButtonHover;
        }
    }

    // The "bottom screen": text in rows and columns, like the 3DS app's console,
    // with pictures and bars drawn among it. What's on it is drawn by whoever
    // handles PaintScreen, through a Painter, which also says what can be
    // clicked: buttons (highlighted under the mouse), and places that can be
    // dragged along (the progress bar).
    sealed class BottomScreen : Control
    {
        public const int Cols = 48;

        public readonly Font Regular, Strong, Label;   // (Label: the buttons')
        public readonly int CellW, RowH, Pad;

        public event Action<Painter> PaintScreen;
        public event Action<int> Wheel;

        internal sealed class Hit
        {
            public Rectangle Rect;
            public Action Click;
            public Action<double, bool> Drag;   // (where along it, 0 to 1; and whether it was let go)
        }

        internal readonly List<Hit> hits = new List<Hit>();
        internal Point mouse = new Point(-1, -1);
        Hit dragging;

        public BottomScreen()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Black;
            Regular = new Font("Consolas", 10f);
            Strong = new Font("Consolas", 10f, FontStyle.Bold);
            Label = new Font("Segoe UI Semibold", 9f);
            Size ten = TextRenderer.MeasureText("MMMMMMMMMM", Regular, Size.Empty, TextFormatFlags.NoPadding);
            CellW = ten.Width / 10;
            RowH = ten.Height + 2;
            Pad = CellW;
        }

        public int PanelWidth => Cols * CellW + 2 * Pad;
        public int Rows => ClientSize.Height / RowH;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            hits.Clear();
            PaintScreen?.Invoke(new Painter(this, e.Graphics));
        }

        Hit HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
                if (hits[i].Rect.Contains(p))
                    return hits[i];
            return null;
        }

        static double Along(Hit h, int x) => h.Rect.Width > 1 ? Math.Max(0, Math.Min(1, (x - h.Rect.X) / (double)(h.Rect.Width - 1))) : 0;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left)
                return;
            Hit h = HitAt(e.Location);
            if (h?.Drag != null)
            {
                dragging = h;
                Capture = true;
                h.Drag(Along(h, e.X), false);
            }
            else
                h?.Click?.Invoke();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != null)
                dragging.Drag(Along(dragging, e.X), false);
            Hit before = HitAt(mouse), now = HitAt(e.Location);
            mouse = e.Location;
            if (before != now)
                Invalidate();
            Cursor = now?.Click != null || now?.Drag != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging != null)
            {
                Hit h = dragging;
                dragging = null;
                Capture = false;
                h.Drag(Along(h, e.X), true);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Wheel?.Invoke(e.Delta);
        }

        // Where row r, column col is (cols wide, rows high).
        public Rectangle Cells(int r, int col, int cols, int rows = 1) =>
            new Rectangle(Pad + col * CellW, r * RowH, cols * CellW, rows * RowH);
    }

    // Draws on the bottom screen, in its rows and columns.
    sealed class Painter
    {
        readonly BottomScreen s;
        public readonly Graphics G;

        public Painter(BottomScreen screen, Graphics g)
        {
            s = screen;
            G = g;
        }

        public int Rows => s.Rows;
        public int RowH => s.RowH;
        public int CellW => s.CellW;
        public Rectangle Cells(int r, int col, int cols, int rows = 1) => s.Cells(r, col, cols, rows);

        // Text at row r, column col, cut to 'max' columns (to the screen's edge if 0).
        public void Text(int r, int col, Style style, string text, int max = 0)
        {
            if (max <= 0)
                max = BottomScreen.Cols - col;
            if (text.Length > max)
                text = text.Substring(0, Math.Max(0, max - 1)) + "…";
            Rectangle at = s.Cells(r, col, text.Length);
            Color? back = Theme.Back(style);
            if (back.HasValue)
                using (Brush b = new SolidBrush(back.Value))
                    G.FillRectangle(b, at);
            TextRenderer.DrawText(G, text, Theme.Bold(style) ? s.Strong : s.Regular, new Point(at.X, at.Y + 1), Theme.Fore(style),
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // A whole row: 'text', on the row's colour from edge to edge (the title bar).
        public void Row(int r, Style style, string text)
        {
            Color? back = Theme.Back(style);
            if (back.HasValue)
                using (Brush b = new SolidBrush(back.Value))
                    G.FillRectangle(b, 0, r * s.RowH, s.ClientSize.Width, s.RowH);
            Text(r, 0, style, text);
        }

        // Text right-aligned on row r.
        public void Right(int r, Style style, string text) => Text(r, BottomScreen.Cols - text.Length, style, text);

        // Word-wraps 'text' over rows first .. last (cut short, with "…", if it
        // doesn't fit); returns the row after it.
        public int Wrap(int first, int last, Style style, string text, int col = 0)
        {
            int width = BottomScreen.Cols - col, r = first;
            while (r <= last && text.Length > 0)
            {
                int cut = text.Length;
                if (r == last)
                    cut = text.Length;
                else if (cut > width)
                {
                    cut = text.LastIndexOf(' ', width);
                    if (cut <= 0)
                        cut = width;
                }
                Text(r++, col, style, text.Substring(0, cut).TrimEnd());
                text = text.Substring(cut).TrimStart();
            }
            return r;
        }

        public int S(int px) => px * s.DeviceDpi / 96;

        // What the button under the mouse does, if it says (set as buttons are drawn).
        public string Tip;

        // The width of the screen's text, from y down, 'height' high.
        public Rectangle Band(int y, int height) => new Rectangle(s.Pad, y, BottomScreen.Cols * s.CellW, height);

        // 'band' cut into n side by side, 'gap' apart.
        public Rectangle[] Split(Rectangle band, int n, int gap)
        {
            Rectangle[] parts = new Rectangle[n];
            for (int i = 0; i < n; i++)
            {
                int x0 = band.X + (band.Width + gap) * i / n, x1 = band.X + (band.Width + gap) * (i + 1) / n - gap;
                parts[i] = new Rectangle(x0, band.Y, x1 - x0, band.Height);
            }
            return parts;
        }

        static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle box, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(box.X, box.Y, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // A rounded box, smoothed.
        public void Box(Rectangle box, Color color, int radius)
        {
            G.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = Rounded(box, radius))
            using (Brush b = new SolidBrush(color))
                G.FillPath(b, path);
            G.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }

        // Text in the middle of 'box' (or at its left, with 'left').
        public void TextIn(Rectangle box, string text, Color color, Font font = null, bool left = false)
        {
            TextRenderer.DrawText(G, text, font ?? s.Label, box, color,
                                  (left ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // A button filling 'box': its label (or picture) in the middle, lighter
        // under the mouse, and darker, its label dim, with no 'click'. 'tip'
        // says what it does, at the bottom of the screen while the mouse is on
        // it.
        public void Button(Rectangle box, string label, Action click, Look look = Look.Normal, Glyph glyph = Glyph.None, string tip = null)
        {
            bool hover = click != null && box.Contains(s.mouse);
            Color face = click == null ? Theme.ButtonOff : look == Look.Red ? (hover ? Theme.RedHover : Theme.Red) :
                         hover ? Theme.ButtonHover : Theme.Button;
            Color fore = click == null ? Theme.Fore(Style.Dim) : Color.White;
            Box(box, face, S(4));
            if (glyph != Glyph.None)
                DrawGlyph(glyph, box, fore);
            if (label != null)
                TextIn(box, label, fore);
            if (click != null)
                Clickable(box, click);
            if (hover && tip != null)
                Tip = tip;
        }

        // A setting that's on or off, filling 'box': its name at the left, and a
        // switch at the right, red when it's on. Clicking anywhere on it changes it.
        public void Switch(Rectangle box, string name, bool on, Action click, string note = null)
        {
            bool hover = box.Contains(s.mouse);
            Box(box, hover ? Theme.ButtonHover : Theme.Button, S(4));
            TextIn(new Rectangle(box.X + S(10), box.Y, box.Width - S(70), box.Height), name, Color.White, null, true);
            if (note != null)
                TextIn(new Rectangle(box.X + S(120), box.Y, box.Width - S(180), box.Height), note, Theme.Fore(Style.Dim), null, true);
            Rectangle pill = new Rectangle(box.Right - S(54), box.Y + (box.Height - S(18)) / 2, S(44), S(18));
            Box(pill, on ? Theme.Red : Theme.SwitchOff, S(9));
            TextIn(pill, on ? "on" : "off", Color.White);
            Clickable(box, click);
        }

        // A picture for a player button: play, pause, and a triangle pointing
        // back or on, small for a frame and big for a second.
        void DrawGlyph(Glyph glyph, Rectangle box, Color color)
        {
            float h = Math.Min(box.Height, box.Width) * 0.42f, cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            float bar = Math.Max(2f, h * 0.18f);
            G.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush b = new SolidBrush(color))
            {
                // A triangle 'size' times play's, pointing right (or left), in the middle.
                void Triangle(float size, bool right)
                {
                    float th = h * size, tw = th * 0.86f;
                    float flat = right ? cx - tw * 0.4f : cx + tw * 0.4f, point = right ? flat + tw : flat - tw;
                    G.FillPolygon(b, new[] { new PointF(flat, cy - th / 2), new PointF(flat, cy + th / 2), new PointF(point, cy) });
                }
                switch (glyph)
                {
                    case Glyph.Play:
                        Triangle(1, true);
                        break;
                    case Glyph.Pause:
                        G.FillRectangle(b, cx - bar * 1.6f, cy - h / 2, bar * 1.1f, h);
                        G.FillRectangle(b, cx + bar * 0.5f, cy - h / 2, bar * 1.1f, h);
                        break;
                    case Glyph.FrameOn:
                        Triangle(0.65f, true);
                        break;
                    case Glyph.FrameBack:
                        Triangle(0.65f, false);
                        break;
                    case Glyph.SecondOn:
                        Triangle(1.3f, true);
                        break;
                    case Glyph.SecondBack:
                        Triangle(1.3f, false);
                        break;
                }
            }
            G.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        }

        public void Clickable(Rectangle at, Action click) => s.hits.Add(new BottomScreen.Hit { Rect = at, Click = click });
        public void Draggable(Rectangle at, Action<double, bool> drag) => s.hits.Add(new BottomScreen.Hit { Rect = at, Drag = drag });
        public bool Hovered(Rectangle at) => at.Contains(s.mouse);

        public void Fill(Rectangle at, Color color)
        {
            using (Brush b = new SolidBrush(color))
                G.FillRectangle(b, at);
        }
    }
}
