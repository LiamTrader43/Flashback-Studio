using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // The colours of the 3DS app's bottom screen (main.c's C_* and COLOR_*).
    enum Style { Normal, Title, Selected, Dim, Key, Warn, Info }

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

    // The "bottom screen": text in rows and columns, like the 3DS app's console,
    // with pictures and bars drawn among it. What's on it is drawn by whoever
    // handles PaintScreen, through a Painter, which also says what can be
    // clicked: buttons (highlighted under the mouse), and places that can be
    // dragged along (the progress bar).
    sealed class BottomScreen : Control
    {
        public const int Cols = 48;

        public readonly Font Regular, Strong;
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

        // Clickable text: highlighted (the selected style) under the mouse.
        // Returns the column after it.
        public int Button(int r, int col, string text, Action click, Style style = Style.Normal)
        {
            Rectangle at = s.Cells(r, col, text.Length);
            bool hover = at.Contains(s.mouse);
            Text(r, col, hover ? Style.Selected : style, text);
            Clickable(at, click);
            return col + text.Length;
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
