using System;
using System.Collections.Generic;
using System.Globalization;

namespace FlashbackStudio
{
    // Where the input display goes on the 3DS's 400 x 240 top screen, how big it
    // is, and its colour, as the player and exports draw it (changed in the
    // player, and remembered). Left and Bottom are how far it is from the
    // screen's left and bottom edges, and Scale how big one of its layout's
    // units is, in the top screen's pixels; Color is its light line's colour
    // (0xRRGGBB). To start with, it's as Flashback Studio draws it on the 3DS.
    struct InputLayout : IEquatable<InputLayout>
    {
        public const float DefaultScale = 0.8f;                 // (Studio's PAD_SCALE)
        public const float MinScale = DefaultScale / 2, MaxScale = DefaultScale * 2.5f;
        public const int DefaultColor = 0xE1E1E1;               // (a light grey: 225)
        public const float LayoutW = 136, LayoutH = 84;         // (the layout's size, in its units)
        const float ScreenW = 400, ScreenH = 240;

        public float Left, Bottom, Scale;
        public int Color;

        public static InputLayout Default => new InputLayout { Left = 6, Bottom = 6, Scale = DefaultScale, Color = DefaultColor };

        public float Width => LayoutW * Scale;
        public float Height => LayoutH * Scale;
        public float Top => ScreenH - Bottom - Height;

        // Between the smallest and biggest sizes, and all of it on the screen.
        public InputLayout Clamped()
        {
            InputLayout l = this;
            l.Scale = float.IsNaN(Scale) ? DefaultScale : Math.Max(MinScale, Math.Min(MaxScale, Scale));
            l.Left = float.IsNaN(Left) ? 0 : Math.Max(0, Math.Min(ScreenW - l.Width, Left));
            l.Bottom = float.IsNaN(Bottom) ? 0 : Math.Max(0, Math.Min(ScreenH - l.Height, Bottom));
            l.Color &= 0xFFFFFF;
            return l;
        }

        // Dragged by (dx, dy) top-screen pixels (down is +y), and kept on the screen.
        public InputLayout Moved(float dx, float dy)
        {
            InputLayout l = this;
            l.Left += dx;
            l.Bottom -= dy;
            return l.Clamped();
        }

        // Resized by dragging its corner 'corner' (0 top left, 1 top right, 2
        // bottom left, 3 bottom right) by (dx, dy) top-screen pixels: the
        // opposite corner stays where it is, and its shape stays the same. It
        // follows the corner across or down, whichever makes it bigger, but no
        // further than the screen's edges.
        public InputLayout Resized(int corner, float dx, float dy)
        {
            bool right = (corner & 1) != 0, bottom = (corner & 2) != 0;
            float x = Left, y = Top;
            float fx = right ? x : x + Width, fy = bottom ? y : y + Height;                 // (the corner that stays)
            float cx = (right ? x + Width : x) + dx, cy = (bottom ? y + Height : y) + dy;   // (where the dragged one is now)
            float scale = Math.Max((right ? cx - fx : fx - cx) / LayoutW, (bottom ? cy - fy : fy - cy) / LayoutH);
            float room = Math.Min((right ? ScreenW - fx : fx) / LayoutW, (bottom ? ScreenH - fy : fy) / LayoutH);
            scale = Math.Max(MinScale, Math.Min(Math.Min(MaxScale, room), scale));
            InputLayout l = this;
            l.Scale = scale;
            l.Left = right ? fx : fx - LayoutW * scale;
            l.Bottom = ScreenH - (bottom ? fy : fy - LayoutH * scale) - LayoutH * scale;
            return l.Clamped();
        }

        public bool Equals(InputLayout o) => Left == o.Left && Bottom == o.Bottom && Scale == o.Scale && Color == o.Color;
        public override bool Equals(object o) => o is InputLayout l && Equals(l);
        public override int GetHashCode() => Left.GetHashCode() ^ Bottom.GetHashCode() * 3 ^ Scale.GetHashCode() * 7 ^ Color;

        // As it's remembered (in settings.txt): "left,bottom,scale,RRGGBB".
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:X6}", Left, Bottom, Scale, Color & 0xFFFFFF);

        // (Kept on the screen; false, and the default, for anything else.)
        public static bool TryParse(string s, out InputLayout layout)
        {
            layout = Default;
            string[] part = (s ?? "").Split(',');
            if (part.Length != 4 ||
                !float.TryParse(part[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float left) ||
                !float.TryParse(part[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float bottom) ||
                !float.TryParse(part[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) ||
                part[3].Length != 6 || !int.TryParse(part[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int color) ||
                float.IsNaN(left) || float.IsNaN(bottom) || float.IsNaN(scale) ||
                float.IsInfinity(left) || float.IsInfinity(bottom) || float.IsInfinity(scale))
                return false;
            layout = new InputLayout { Left = left, Bottom = bottom, Scale = scale, Color = color }.Clamped();
            return true;
        }
    }

    // The input display, drawn over a video frame as Flashback Studio draws it
    // over a clip (see drawInputs in 3ds-app/source/main.c, and keep the two
    // alike): a picture of the controls, in the bottom left corner to start with
    // (or where an InputLayout says, as big and in the colour it says). Each
    // control is a light line between two black edges, smoothed into the
    // picture, and filled in while it was held during the frame; the Circle Pad
    // shows where its knob was, and the touch screen (darkened inside) where it
    // was touched.
    // It's laid out at 136 x 84 (the coordinates below), mirrored about the touch
    // screen's middle, and drawn at the layout's scale on the 3DS's 400 x 240 top
    // screen (Flashback Studio's 0.8 to start with), times how much bigger than
    // that the frame is.
    sealed class InputOverlay
    {
        const int ScreenHeight = 240;       // the top screen's

        // The Circle Pad's middle, and how far its knob can go from it.
        const float StickX = 25, StickY = 29, StickReach = 5;
        // Where a touch can be shown, inside the touch screen.
        const float TouchX0 = 57, TouchY0 = 30, TouchW = 26, TouchH = 16;

        // The buttons, by their index in a Pixel's Key.
        static readonly uint[] Keys =
        {
            1u << 9,    // L
            1u << 8,    // R
            1u << 6,    // D-Pad up
            1u << 7,    // D-Pad down
            1u << 5,    // D-Pad left
            1u << 4,    // D-Pad right
            1u << 10,   // X
            1u << 11,   // Y
            1u << 0,    // A
            1u << 1,    // B
            1u << 2,    // SELECT
            1u << 3,    // START
        };
        const byte NoKey = 0xFF;

        // A pixel the controls cover (worked out once, from their shapes), and how
        // it covers the frame while its button isn't held [0] and is [1].
        struct Pixel
        {
            public int Offset;              // in the frame, from the display's top left corner
            public byte Key;                // index into Keys, or NoKey
            public bool Darken;             // inside the touch screen: the frame is darkened under it
            public byte Alpha0, Alpha1;     // how much of the frame it covers, out of 255...
            public float Level0, Level1;    // ...and with what: 0 black, to 1 the light line's colour
        }

        readonly int width, height;         // the frame's
        public int Width => width;
        public int Height => height;
        readonly float layoutScale;         // the layout's, on the top screen...
        readonly float scale;               // ...and in the frame
        readonly int boxW, boxH;            // the display's size in the frame...
        int x0, y0;                         // ...and where it is in it
        readonly int[] light = new int[3];  // the light line's colour (blue, green and red, as in the frame)
        readonly Pixel[] pixels;

        // Where the display is in the frame.
        public int X => x0;
        public int Y => y0;
        public int BoxWidth => boxW;
        public int BoxHeight => boxH;

        // For video frames 'width' x 'height' (BGR, 3 bytes a pixel, top row
        // first), where it starts, or where 'layout' says.
        public InputOverlay(int width, int height) : this(width, height, InputLayout.Default)
        {
        }

        public InputOverlay(int width, int height, InputLayout layout)
        {
            layout = layout.Clamped();
            this.width = width;
            this.height = height;
            layoutScale = layout.Scale;
            scale = layout.Scale * height / ScreenHeight;
            boxW = Math.Min(width, (int)Math.Ceiling(136 * scale));
            boxH = Math.Min(height, (int)Math.Ceiling(84 * scale));
            pixels = BuildPixels(boxW, boxH);
            Place(layout);
        }

        // Whether it's drawn at the size 'layout' says, for frames width x height
        // (so Place can move it to where that says, in its colour).
        public bool Fits(int width, int height, InputLayout layout) =>
            width == this.width && height == this.height && layout.Clamped().Scale == layoutScale;

        // Moves it to where 'layout' says, and gives it its colour (but not its
        // size: see Fits).
        public void Place(InputLayout layout)
        {
            layout = layout.Clamped();
            x0 = Math.Max(0, Math.Min(width - boxW, (int)Math.Round(layout.Left * height / ScreenHeight)));
            y0 = Math.Max(0, Math.Min(height - boxH, height - boxH - (int)Math.Round(layout.Bottom * height / ScreenHeight)));
            light[0] = layout.Color & 0xFF;
            light[1] = (layout.Color >> 8) & 0xFF;
            light[2] = (layout.Color >> 16) & 0xFF;
        }

        // How far (in layout units, negative inside) point (x, y) is from the edge
        // of a rounded rectangle centred on (cx, cy), hw and hh from its middle to
        // its sides, with corners of radius r; or from the edge of a circle.
        static float RoundRect(float x, float y, float cx, float cy, float hw, float hh, float r)
        {
            float qx = Math.Abs(x - cx) - (hw - r), qy = Math.Abs(y - cy) - (hh - r);
            float ox = qx > 0 ? qx : 0, oy = qy > 0 ? qy : 0;
            float inside = qx > qy ? qx : qy;
            return (float)Math.Sqrt(ox * ox + oy * oy) + (inside < 0 ? inside : 0) - r;
        }

        static float CircleEdge(float x, float y, float cx, float cy, float r)
        {
            return (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
        }

        static float Clamp(float v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        static byte Byte(float v)
        {
            return (byte)(v * 255 + 0.5f);
        }

        // How a control covers a pixel 'd' pixels from its edge (negative inside):
        // a black edge 'scale' pixels wide, then the light line, twice as wide, then
        // another black edge, and inside that the frame, or the light line's colour
        // while it's held. Each change is smoothed over a pixel.
        void Outline(float d, bool held, out byte alpha, out float level)
        {
            float e = scale;
            float a = Clamp(e + 0.5f - d);
            level = Math.Min(Clamp(0.5f - d), Clamp(d + 2 * e + 0.5f));
            if (held)
                level = Math.Max(level, Clamp(-3 * e + 0.5f - d));
            else
                a = Math.Min(a, Clamp(d + 3 * e + 0.5f));
            alpha = Byte(a);
        }

        // The controls, in the order BuildPixels measures them: their buttons
        // (the D-Pad's are its arms, below).
        static readonly byte[] ControlKeys = { 0, 1, NoKey, NoKey, NoKey, 10, 11, 6, 7, 8, 9 };
        const int DPad = 3, TouchScreen = 4;

        Pixel[] BuildPixels(int w, int h)
        {
            List<Pixel> list = new List<Pixel>();
            float[] dist = new float[ControlKeys.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float px = (x + 0.5f) / scale, py = (y + 0.5f) / scale;     // (in the layout)
                    float dx = px - 25, dy = py - 64;   // (from the D-Pad's middle)
                    dist[0] = RoundRect(px, py, 30, 9, 24, 5, 5);               // L
                    dist[1] = RoundRect(px, py, 110, 9, 24, 5, 5);              // R
                    dist[2] = CircleEdge(px, py, StickX, StickY, 12);           // the Circle Pad
                    // The D-Pad: one cross, its inside shared out between its arms.
                    dist[DPad] = Math.Min(RoundRect(px, py, 25, 64, 19, 7, 2), RoundRect(px, py, 25, 64, 7, 19, 2));
                    dist[TouchScreen] = RoundRect(px, py, 70, 38, 19, 14, 3);   // the touch screen
                    dist[5] = RoundRect(px, py, 60, 66, 8, 5, 5);               // SELECT
                    dist[6] = RoundRect(px, py, 80, 66, 8, 5, 5);               // START
                    dist[7] = CircleEdge(px, py, 115, 38, 7);                   // X
                    dist[8] = CircleEdge(px, py, 103, 50, 7);                   // Y
                    dist[9] = CircleEdge(px, py, 127, 50, 7);                   // A
                    dist[10] = CircleEdge(px, py, 115, 62, 7);                  // B

                    // The control nearest the pixel (they don't touch), and how far
                    // it is from its edge in pixels.
                    int n = 0;
                    for (int i = 1; i < dist.Length; i++)
                        if (dist[i] < dist[n])
                            n = i;
                    float d = dist[n] * scale;
                    Pixel p = new Pixel
                    {
                        Offset = (y * width + x) * 3,
                        Key = n != DPad ? ControlKeys[n] :
                              Math.Abs(dx) < 3 && Math.Abs(dy) < 3 ? NoKey :
                              Math.Abs(dy) > Math.Abs(dx) ? (dy < 0 ? (byte)2 : (byte)3) : (dx < 0 ? (byte)4 : (byte)5),
                        Darken = n == TouchScreen && d <= -2 * scale,     // (under its inner black edge)
                    };
                    Outline(d, false, out p.Alpha0, out p.Level0);
                    Outline(d, p.Key != NoKey, out p.Alpha1, out p.Level1);
                    if (p.Alpha0 != 0 || p.Alpha1 != 0 || p.Darken)
                        list.Add(p);
                }
            return list.ToArray();
        }

        // 'over' on 'under', 'alpha' (out of 255) of the way.
        static byte Blend(int under, int over, int alpha)
        {
            return (byte)((over * alpha + under * (255 - alpha) + 127) / 255);
        }

        // A dot centred on (cx, cy), r in radius, in the layout (the Circle Pad's
        // knob, or a touch): light, with a black edge, smoothed.
        void Dot(byte[] frame, float cx, float cy, float r)
        {
            cx *= scale;
            cy *= scale;
            r *= scale;
            for (int x = (int)(cx - r) - 2; x <= (int)(cx + r) + 2; x++)
                for (int y = (int)(cy - r) - 2; y <= (int)(cy + r) + 2; y++)
                {
                    int fx = x0 + x, fy = y0 + y;
                    if (fx < 0 || fy < 0 || fx >= width || fy >= height)
                        continue;
                    float d = (float)Math.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) - r;
                    int alpha = Byte(Clamp(scale + 0.5f - d));
                    if (alpha != 0)
                    {
                        float level = Clamp(0.5f - d);
                        int o = (fy * width + fx) * 3;
                        for (int c = 0; c < 3; c++)
                            frame[o + c] = Blend(frame[o + c], (int)(light[c] * level + 0.5f), alpha);
                    }
                }
        }

        // Draws the display for 'input' over 'frame'.
        public void Draw(byte[] frame, FlbInput input)
        {
            uint b = input.Buttons;
            int at = (y0 * width + x0) * 3;
            foreach (Pixel p in pixels)
            {
                bool held = p.Key != NoKey && (b & Keys[p.Key]) != 0;
                int alpha = held ? p.Alpha1 : p.Alpha0, o = at + p.Offset;
                float level = held ? p.Level1 : p.Level0;
                for (int c = 0; c < 3; c++)
                {
                    int under = p.Darken ? frame[o + c] >> 1 : frame[o + c];   // (darkened: halved)
                    frame[o + c] = Blend(under, (byte)(light[c] * level + 0.5f), alpha);
                }
            }

            // The Circle Pad's knob, where it was (as far as it reaches)...
            float kx = input.CircleX * StickReach / 156f, ky = input.CircleY * StickReach / 156f;
            float k = (float)Math.Sqrt(kx * kx + ky * ky);
            if (k > StickReach)
            {
                kx *= StickReach / k;
                ky *= StickReach / k;
            }
            Dot(frame, StickX + kx, StickY - ky, 3);

            // ...and where the touch screen was touched (320 x 240).
            if ((b & FlbInput.KeyTouch) != 0)
            {
                int tx = Math.Min((int)input.TouchX, 319), ty = Math.Min((int)input.TouchY, 239);
                Dot(frame, TouchX0 + tx * TouchW / 320f, TouchY0 + ty * TouchH / 240f, 2);
            }
        }
    }
}
