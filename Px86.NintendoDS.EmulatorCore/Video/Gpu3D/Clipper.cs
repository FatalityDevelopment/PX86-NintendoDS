namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// Clips a polygon against the six view-volume planes in clip space
    /// (−w ≤ x, y, z ≤ w), Sutherland–Hodgman style. New vertices sit where
    /// the edges cross a plane, with colour and texture coordinates
    /// interpolated linearly in clip space.
    ///
    /// Reference: GBATEK "DS 3D Polygon Definitions by Vertices" (Clipping).
    /// </summary>
    public static class Clipper
    {
        public static List<Vertex3D> Clip(Vertex3D[] polygon)
        {
            var current = new List<Vertex3D>(polygon);
            for (int plane = 0; plane < 6 && current.Count > 0; plane++)
                current = ClipPlane(current, plane);
            return current;
        }

        // Signed distance to the plane; ≥ 0 means inside.
        private static long Distance(in Vertex3D v, int plane) => plane switch
        {
            0 => (long)v.W - v.X,   // right   x ≤ w
            1 => (long)v.W + v.X,   // left   −w ≤ x
            2 => (long)v.W - v.Y,   // top     y ≤ w
            3 => (long)v.W + v.Y,   // bottom −w ≤ y
            4 => (long)v.W - v.Z,   // far     z ≤ w
            _ => (long)v.W + v.Z,   // near   −w ≤ z
        };

        private static List<Vertex3D> ClipPlane(List<Vertex3D> input, int plane)
        {
            var output = new List<Vertex3D>(input.Count + 2);
            for (int i = 0; i < input.Count; i++)
            {
                var a = input[i];
                var b = input[(i + 1) % input.Count];
                long da = Distance(a, plane);
                long db = Distance(b, plane);

                if (da >= 0)
                    output.Add(a);
                if ((da >= 0) != (db >= 0))
                    output.Add(Interpolate(a, b, da, da - db));
            }
            return output;
        }

        private static Vertex3D Interpolate(in Vertex3D a, in Vertex3D b, long numerator, long denominator)
        {
            double t = denominator == 0 ? 0 : numerator / (double)denominator;
            int Lerp(int x, int y) => (int)Math.Round(x + (y - x) * t);
            return new Vertex3D
            {
                X = Lerp(a.X, b.X), Y = Lerp(a.Y, b.Y), Z = Lerp(a.Z, b.Z), W = Lerp(a.W, b.W),
                R = Lerp(a.R, b.R), G = Lerp(a.G, b.G), B = Lerp(a.B, b.B),
                S = Lerp(a.S, b.S), T = Lerp(a.T, b.T),
            };
        }
    }
}
