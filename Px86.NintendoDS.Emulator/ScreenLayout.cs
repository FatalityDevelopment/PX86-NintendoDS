namespace Px86.NintendoDS.Emulator
{
    public enum ScreenArrangement
    {
        Vertical,
        Horizontal,
        TopOnly,
        BottomOnly,
    }

    /// <summary>A rectangle in window framebuffer pixels (origin top-left, y down).</summary>
    public readonly record struct ScreenRect(float X, float Y, float Width, float Height)
    {
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public bool Contains(float px, float py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
    }

    /// <summary>
    /// Where the two 256×192 screens go inside the window: arrangement, gap,
    /// swap, rotation (multiples of 90° clockwise) and optional integer
    /// scaling, letterboxed in the area below the menu bar. Also maps a mouse
    /// position back to touch-screen pixels. Pure geometry, so it is unit
    /// tested; the renderer and the mouse handler share one instance.
    /// </summary>
    public sealed class ScreenLayout
    {
        public const int ScreenWidth = 256;
        public const int ScreenHeight = 192;

        public ScreenArrangement Arrangement { get; init; } = ScreenArrangement.Vertical;

        /// <summary>Gap between the screens, in DS pixels (scaled with the picture).</summary>
        public int Gap { get; init; }

        /// <summary>Show the bottom (touch) screen first.</summary>
        public bool Swap { get; init; }

        /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
        public int Rotation { get; init; }

        public bool IntegerScale { get; init; }

        /// <summary>Rectangles of the top and bottom DS screens (empty if hidden).</summary>
        public (ScreenRect Top, ScreenRect Bottom) Compute(ScreenRect area)
        {
            // Lay the screens out unrotated in DS pixels.
            bool vertical = Arrangement == ScreenArrangement.Vertical;
            bool both = Arrangement is ScreenArrangement.Vertical or ScreenArrangement.Horizontal;
            float contentW = Arrangement == ScreenArrangement.Horizontal ? ScreenWidth * 2 + Gap : ScreenWidth;
            float contentH = vertical ? ScreenHeight * 2 + Gap : ScreenHeight;

            bool quarterTurn = Rotation is 90 or 270;
            float rotatedW = quarterTurn ? contentH : contentW;
            float rotatedH = quarterTurn ? contentW : contentH;

            float scale = MathF.Min(area.Width / rotatedW, area.Height / rotatedH);
            if (IntegerScale && scale >= 1)
                scale = MathF.Floor(scale);
            if (scale <= 0)
                return (default, default);

            float outW = rotatedW * scale, outH = rotatedH * scale;
            float originX = area.X + (area.Width - outW) / 2, originY = area.Y + (area.Height - outH) / 2;

            // Unrotated positions (DS pixels) of the first and second screen.
            var first = new ScreenRect(0, 0, ScreenWidth, ScreenHeight);
            var second = vertical ? new ScreenRect(0, ScreenHeight + Gap, ScreenWidth, ScreenHeight)
                                  : new ScreenRect(ScreenWidth + Gap, 0, ScreenWidth, ScreenHeight);
            ScreenRect top, bottom;
            if (both)
                (top, bottom) = Swap ? (second, first) : (first, second);
            else if (Arrangement == ScreenArrangement.TopOnly)
                (top, bottom) = (first, default);
            else
                (top, bottom) = (default, first);

            ScreenRect Place(ScreenRect r)
            {
                if (r.IsEmpty) return r;
                var (x, y, w, h) = RotateRect(r, contentW, contentH);
                return new ScreenRect(originX + x * scale, originY + y * scale, w * scale, h * scale);
            }
            return (Place(top), Place(bottom));
        }

        /// <summary>Rotate a rectangle inside a content box of size (cw, ch).</summary>
        private (float X, float Y, float W, float H) RotateRect(ScreenRect r, float cw, float ch) => Rotation switch
        {
            90 => (ch - r.Y - r.Height, r.X, r.Height, r.Width),
            180 => (cw - r.X - r.Width, ch - r.Y - r.Height, r.Width, r.Height),
            270 => (r.Y, cw - r.X - r.Width, r.Height, r.Width),
            _ => (r.X, r.Y, r.Width, r.Height),
        };

        /// <summary>Window position → touch-screen pixel (0–255, 0–191), or null when outside the bottom screen.</summary>
        public (int X, int Y)? ToTouch(ScreenRect area, float px, float py)
        {
            var bottom = Compute(area).Bottom;
            if (bottom.IsEmpty || !bottom.Contains(px, py))
                return null;
            float u = (px - bottom.X) / bottom.Width, v = (py - bottom.Y) / bottom.Height;
            (u, v) = Rotation switch
            {
                90 => (v, 1 - u),
                180 => (1 - u, 1 - v),
                270 => (1 - v, u),
                _ => (u, v),
            };
            return (Math.Clamp((int)(u * ScreenWidth), 0, ScreenWidth - 1), Math.Clamp((int)(v * ScreenHeight), 0, ScreenHeight - 1));
        }

        /// <summary>
        /// Texture coordinates of a screen rectangle's corners in the order
        /// top-left, top-right, bottom-left, bottom-right (window space), for the
        /// DS screen occupying v0..v1 of the frame texture.
        /// </summary>
        public (float U, float V)[] CornerUvs(float v0, float v1)
        {
            (float, float) tl = (0, v0), tr = (1, v0), bl = (0, v1), br = (1, v1);
            return Rotation switch
            {
                90 => new[] { bl, tl, br, tr },
                180 => new[] { br, bl, tr, tl },
                270 => new[] { tr, br, tl, bl },
                _ => new[] { tl, tr, bl, br },
            };
        }

        /// <summary>Window size for a scale factor (used for the initial window and "scale" presets).</summary>
        public (int Width, int Height) WindowSize(int scale)
        {
            float w = Arrangement == ScreenArrangement.Horizontal ? ScreenWidth * 2 + Gap : ScreenWidth;
            float h = Arrangement == ScreenArrangement.Vertical ? ScreenHeight * 2 + Gap : ScreenHeight;
            if (Rotation is 90 or 270) (w, h) = (h, w);
            return ((int)(w * scale), (int)(h * scale));
        }
    }
}
