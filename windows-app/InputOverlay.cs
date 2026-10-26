using System;
using System.Collections.Generic;

namespace FlashbackConverter
{
    // The input display, drawn over a video frame as Flashback Studio draws it
    // over a clip (see drawInputs in 3ds-app/source/main.c, and keep the two
    // alike): a picture of the controls in the bottom left corner. Each control is
    // a light line between two black edges, smoothed into the picture, and filled
    // in while it was held during the frame; the Circle Pad shows where its knob
    // was, and the touch screen (darkened inside) where it was touched.
    // It's laid out at 136 x 84 (the coordinates below), mirrored about the touch
    // screen's middle, and drawn at the same size next to the picture as in
    // Flashback Studio, which draws it at 0.8 of that on the 3DS's 400 x 240 top
    // screen.
    sealed class InputOverlay
    {
        const float StudioScale = 0.8f;     // (Studio's PAD_SCALE)
        const int StudioMargin = 6;         // from the left and bottom edges, on the top screen
        const int ScreenHeight = 240;       // the top screen's
        const int Light = 225;              // the light line's grey

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
            public int Offset;              // in the frame
            public byte Key;                // index into Keys, or NoKey
            public bool Darken;             // inside the touch screen: the frame is darkened under it
            public byte Alpha0, Alpha1;     // how much of the frame it covers, out of 255...
            public byte Grey0, Grey1;       // ...and with what
        }

        readonly int width, height;         // the frame's
        readonly float scale;               // the layout's, in the frame
        readonly int x0, y0;                // where it is in the frame
        readonly Pixel[] pixels;

        // For video frames 'width' x 'height' (BGR, 3 bytes a pixel, top row first).
        public InputOverlay(int width, int height)
        {
            this.width = width;
            this.height = height;
            scale = StudioScale * height / ScreenHeight;
            int w = (int)Math.Ceiling(136 * scale), h = (int)Math.Ceiling(84 * scale);
            x0 = (int)Math.Round((float)StudioMargin * height / ScreenHeight);
            y0 = height - h - x0;
            pixels = BuildPixels(w, h);
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
        void Outline(float d, bool held, out byte alpha, out byte grey)
        {
            float e = scale;
            float a = Clamp(e + 0.5f - d);
            float level = Math.Min(Clamp(0.5f - d), Clamp(d + 2 * e + 0.5f));
            if (held)
                level = Math.Max(level, Clamp(-3 * e + 0.5f - d));
            else
                a = Math.Min(a, Clamp(d + 3 * e + 0.5f));
            alpha = Byte(a);
            grey = (byte)(Light * level + 0.5f);
        }

        Pixel[] BuildPixels(int w, int h)
        {
            List<Pixel> list = new List<Pixel>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float px = (x + 0.5f) / scale, py = (y + 0.5f) / scale;     // (in the layout)
                    float dx = px - 25, dy = py - 64;   // (from the D-Pad's middle)
                    (float D, byte Key, bool Darken)[] controls =
                    {
                        (RoundRect(px, py, 30, 9, 24, 5, 5), 0, false),                 // L
                        (RoundRect(px, py, 110, 9, 24, 5, 5), 1, false),                // R
                        (CircleEdge(px, py, StickX, StickY, 12), NoKey, false),         // the Circle Pad
                        // The D-Pad: one cross, its inside shared out between its arms.
                        (Math.Min(RoundRect(px, py, 25, 64, 19, 7, 2), RoundRect(px, py, 25, 64, 7, 19, 2)),
                         Math.Abs(dx) < 3 && Math.Abs(dy) < 3 ? NoKey :
                         Math.Abs(dy) > Math.Abs(dx) ? (dy < 0 ? (byte)2 : (byte)3) : (dx < 0 ? (byte)4 : (byte)5), false),
                        (RoundRect(px, py, 70, 38, 19, 14, 3), NoKey, true),            // the touch screen
                        (RoundRect(px, py, 60, 66, 8, 5, 5), 10, false),                // SELECT
                        (RoundRect(px, py, 80, 66, 8, 5, 5), 11, false),                // START
                        (CircleEdge(px, py, 115, 38, 7), 6, false),                     // X
                        (CircleEdge(px, py, 103, 50, 7), 7, false),                     // Y
                        (CircleEdge(px, py, 127, 50, 7), 8, false),                     // A
                        (CircleEdge(px, py, 115, 62, 7), 9, false),                     // B
                    };

                    // The control nearest the pixel (they don't touch), and how far
                    // it is from its edge in pixels.
                    int n = 0;
                    for (int i = 1; i < controls.Length; i++)
                        if (controls[i].D < controls[n].D)
                            n = i;
                    float d = controls[n].D * scale;
                    Pixel p = new Pixel
                    {
                        Offset = ((y0 + y) * width + x0 + x) * 3,
                        Key = controls[n].Key,
                        Darken = controls[n].Darken && d <= -2 * scale,     // (under its inner black edge)
                    };
                    Outline(d, false, out p.Alpha0, out p.Grey0);
                    Outline(d, p.Key != NoKey, out p.Alpha1, out p.Grey1);
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
                        int grey = (int)(Light * Clamp(0.5f - d) + 0.5f), o = (fy * width + fx) * 3;
                        for (int c = 0; c < 3; c++)
                            frame[o + c] = Blend(frame[o + c], grey, alpha);
                    }
                }
        }

        // Draws the display for 'input' over 'frame'.
        public void Draw(byte[] frame, FlbInput input)
        {
            uint b = input.Buttons;
            foreach (Pixel p in pixels)
            {
                bool held = p.Key != NoKey && (b & Keys[p.Key]) != 0;
                int alpha = held ? p.Alpha1 : p.Alpha0, grey = held ? p.Grey1 : p.Grey0;
                for (int c = 0; c < 3; c++)
                {
                    int under = p.Darken ? frame[p.Offset + c] >> 1 : frame[p.Offset + c];   // (darkened: halved)
                    frame[p.Offset + c] = Blend(under, grey, alpha);
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
