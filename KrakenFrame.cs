using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Gauge;

/// <summary>
/// Draws the Kraken Z3's 320×320 screen. "Single Infographic": a 270° gradient ring with a dot at the reading
/// ("Radial Fill") or a full-screen gradient ("Full Fill"), with the number, its unit and a label. "Dual
/// Infographic": two readings side by side, each with an arc. Also turns frames for the rotation dial.
/// The ring geometry and colours follow NZXT CAM's behaviour; type is Bahnschrift (Windows' own DIN face)
/// and the logo is Gauge's mark, so nothing of CAM's fonts or branding is used.
/// </summary>
internal static class KrakenFrame
{
    const int N = KrakenSession.Size;
    const float Half = N / 2f;

    // Gauge geometry in normalised units (±1 at the edges), scale S as measured for the Z3
    const float S = 0.95f, Ro = S, Ri = S * (1 - 0.15f * S), Rm = S * (1 - 0.075f * S), Rc = (Ro - Ri) / 2, Rd = 0.5f * 0.15f * S;
    static readonly float s_gap = 0.089f * MathF.PI * S; // radians either side of the dot

    public static (float Min, float Max, string Unit, string Label) Range(LcdReading r) => r switch
    {
        LcdReading.LiquidTemp => (0, 105, "°", "Liquid"),
        LcdReading.CpuTemp => (0, 105, "°", "CPU"),
        LcdReading.GpuTemp => (0, 105, "°", "GPU"),
        LcdReading.CpuLoad => (0, 100, "%", "CPU"),
        LcdReading.GpuLoad => (0, 100, "%", "GPU"),
        LcdReading.CpuClock => (0, 9999, "MHz", "CPU"),
        _ => (0, 9999, "MHz", "GPU"),
    };

    /// <summary>The reading's position along the gauge, 0..1, or NaN when there is no reading.</summary>
    public static float Normalise(LcdReading reading, float value)
    {
        var (min, max, _, _) = Range(reading);
        return float.IsNaN(value) ? float.NaN : (Math.Clamp(value, min, max) - min) / (max - min);
    }

    public static Bitmap Render(LcdSettings s, float value)
    {
        var lut = Gradient(s.Color1, s.Color2);
        float r = Normalise(s.Reading, value);
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            int* px = (int*)data.Scan0;
            if (s.Fill) FullFill(px, data.Stride / 4, s);
            else Ring(px, data.Stride / 4, s.Background, lut, r);
        }
        bmp.UnlockBits(data);
        using (var g = Graphics.FromImage(bmp)) Text(g, s, value);
        return bmp;
    }

    // ------------------------------------------------------------------ the ring

    static unsafe void Ring(int* px, int stride, Color bg, Color[] lut, float r)
    {
        // Two arc segments either side of the dot, each with round ends; with no reading, one whole arc.
        var segments = new List<(float From, float To)>();
        float theta = float.IsNaN(r) ? float.NaN : Deg(225) - Deg(270) * r;
        float lo = Deg(-45), hi = Deg(225);
        if (float.IsNaN(theta)) segments.Add((lo, hi));
        else
        {
            if (theta - s_gap > lo) segments.Add((lo, theta - s_gap));
            if (theta + s_gap < hi) segments.Add((theta + s_gap, hi));
        }
        var dot = float.IsNaN(theta) ? default : new PointF(Rm * MathF.Cos(theta), Rm * MathF.Sin(theta));
        var dotColour = float.IsNaN(r) ? default : lut[(int)MathF.Round(r * 255)];

        for (int py = 0; py < N; py++)
        {
            int* row = px + py * stride;
            for (int pxi = 0; pxi < N; pxi++)
            {
                float x = (pxi + 0.5f - Half) / Half, y = (Half - (py + 0.5f)) / Half;
                // Coverage from signed distance, about one pixel of anti-aliasing
                float d = float.MaxValue;
                foreach (var (from, to) in segments) d = MathF.Min(d, ArcDistance(x, y, from, to));
                float ring = Math.Clamp(0.5f - (d - Rc) * Half, 0, 1);
                Color c = bg;
                if (ring > 0) c = Blend(c, lut[(int)MathF.Round(Progress(x, y) * 255)], ring);
                if (!float.IsNaN(theta))
                {
                    float dd = MathF.Sqrt((x - dot.X) * (x - dot.X) + (y - dot.Y) * (y - dot.Y)) - Rd;
                    float cover = Math.Clamp(0.5f - dd * Half, 0, 1);
                    if (cover > 0) c = Blend(c, dotColour, cover);
                }
                row[pxi] = c.ToArgb() | unchecked((int)0xFF000000);
            }
        }
    }

    static float Deg(float d) => d * MathF.PI / 180;

    /// <summary>Distance from (x, y) to the arc's centre line, radius Rm from angle <paramref name="from"/> to <paramref name="to"/> (radians, counter-clockwise).</summary>
    static float ArcDistance(float x, float y, float from, float to)
    {
        float a = MathF.Atan2(y, x);
        if (a < Deg(-90)) a += 2 * MathF.PI; // the arcs live in [-90°, 270°): the opening is at the bottom
        if (a >= from && a <= to) return MathF.Abs(MathF.Sqrt(x * x + y * y) - Rm);
        float d1 = Dist(x, y, Rm * MathF.Cos(from), Rm * MathF.Sin(from)), d2 = Dist(x, y, Rm * MathF.Cos(to), Rm * MathF.Sin(to));
        return MathF.Min(d1, d2);
    }

    static float Dist(float x, float y, float ax, float ay) => MathF.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));

    /// <summary>Progress along the gauge at a point, 0 at the lower left through the top to 1 at the lower right.</summary>
    static float Progress(float x, float y)
    {
        float angle = -MathF.Atan2(y, x) + 2 * MathF.PI;
        float a01 = angle / (2 * MathF.PI) + 0.75f;
        a01 -= MathF.Floor(a01);
        const float offset = 0.125f;
        return a01 < offset ? 0 : a01 > 1 - offset ? 1 : (a01 - offset) / (1 - 2 * offset);
    }

    static unsafe void FullFill(int* px, int stride, LcdSettings s)
    {
        for (int py = 0; py < N; py++)
        {
            int* row = px + py * stride;
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N;
                row[x] = Lerp(s.Color1, s.Color2, u).ToArgb() | unchecked((int)0xFF000000);
            }
        }
    }

    static Color Lerp(Color a, Color b, float t) => Color.FromArgb(255,
        (int)MathF.Round(a.R + (b.R - a.R) * t), (int)MathF.Round(a.G + (b.G - a.G) * t), (int)MathF.Round(a.B + (b.B - a.B) * t));

    static Color Blend(Color under, Color over, float alpha) => Lerp(under, over, alpha);

    // ------------------------------------------------------------------ gradient (OKLab, stops 0.30 and 0.85)

    public static Color[] Gradient(Color c1, Color c2)
    {
        var a = OkLab(c1);
        var b = OkLab(c2);
        var lut = new Color[256];
        for (int i = 0; i < 256; i++)
        {
            float p = i / 255f, t = Math.Clamp((p - 0.30f) / (0.85f - 0.30f), 0, 1);
            lut[i] = FromOkLab(a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);
        }
        return lut;
    }

    static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    static (float L, float A, float B) OkLab(Color c)
    {
        float r = ToLinear(c.R / 255f), g = ToLinear(c.G / 255f), b = ToLinear(c.B / 255f);
        float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    static Color FromOkLab(float L, float A, float B)
    {
        float l = L + 0.3963377774f * A + 0.2158037573f * B, m = L - 0.1055613458f * A - 0.0638541728f * B, s = L - 0.0894841775f * A - 1.2914855480f * B;
        l *= l * l; m *= m * m; s *= s * s;
        float r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        static int Byte(float v) => (int)MathF.Round(Math.Clamp(ToSrgb(Math.Clamp(v, 0, 1)), 0, 1) * 255);
        return Color.FromArgb(255, Byte(r), Byte(g), Byte(b));
    }

    // ------------------------------------------------------------------ text

    /// <summary>The logo (Gauge's mark), the number with its unit, and the label, at CAM's positions.</summary>
    static void Text(Graphics g, LcdSettings s, float value)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var family = new FontFamily("Bahnschrift");
        using var ink = new SolidBrush(s.Ink);
        var (_, _, unit, label) = Range(s.Reading);

        if (s.Logo) Brand.DrawMark(g, Half, 104, 36, s.Ink, Color.FromArgb(80, s.Ink), 9);

        // Number: digits 65 px tall for one or two digits, smaller for three and four so they clear the ring
        string number = float.IsNaN(value) ? "--" : ((int)MathF.Round(value)).ToString();
        float digitHeight = number.Length <= 2 ? 65 : number.Length == 3 ? 59.8f : 44.8f;
        float em = EmFor(family, FontStyle.Bold, "0", digitHeight);
        using var numberPath = PathFor(family, FontStyle.Bold, number, em);
        var nb = numberPath.GetBounds();
        float baseline = 205, top = baseline - digitHeight;
        Place(numberPath, Half - (nb.X + nb.Width / 2), top - nb.Y);
        g.FillPath(ink, numberPath);
        var placed = numberPath.GetBounds();

        // Unit: 40/90 of the number's size, top-aligned with the digits, 7 px to the right
        using var unitPath = PathFor(family, FontStyle.Bold, float.IsNaN(value) ? "" : unit, em * 40 / 90);
        var ub = unitPath.GetBounds();
        Place(unitPath, placed.Right + 7 - ub.X, top - ub.Y);
        g.FillPath(ink, unitPath);

        // Label: capitals 17 px tall, baseline at 250
        float labelEm = EmFor(family, FontStyle.Bold, "L", 17);
        using var labelPath = PathFor(family, FontStyle.Bold, label, labelEm);
        var lb = labelPath.GetBounds();
        using var capPath = PathFor(family, FontStyle.Bold, "L", labelEm);
        var cb = capPath.GetBounds(); // the label's cap line, so descenders hang below the baseline
        Place(labelPath, Half - (lb.X + lb.Width / 2), 250 - 17 - cb.Y);
        g.FillPath(ink, labelPath);
    }

    static GraphicsPath PathFor(FontFamily family, FontStyle style, string text, float em)
    {
        // Winding fill: the font's overlapping contours would otherwise cut holes in the glyphs.
        var path = new GraphicsPath(FillMode.Winding);
        path.AddString(text, family, (int)style, em, PointF.Empty, StringFormat.GenericTypographic);
        return path;
    }

    /// <summary>The em size at which <paramref name="sample"/> is <paramref name="height"/> pixels tall.</summary>
    static float EmFor(FontFamily family, FontStyle style, string sample, float height)
    {
        using var probe = PathFor(family, style, sample, 100);
        return 100 * height / probe.GetBounds().Height;
    }

    static void Place(GraphicsPath path, float dx, float dy)
    {
        using var m = new Matrix();
        m.Translate(dx, dy);
        path.Transform(m);
    }

    // ------------------------------------------------------------------ Dual Infographic ("Vertical")

    // Arc band 0.800..0.988 around radius 0.894, so its round caps (radius 0.094) are half its width; track 0.805..0.988
    const float DualMid = 0.894f, DualHalf = 0.094f, TrackIn = 0.805f, TrackOut = 0.988f;
    /// <summary>How far each arc reaches either side of its centre line at a full reading, in degrees.</summary>
    public const float DualReach = 72;
    /// <summary>Where the two text columns sit, in pixels from the left.</summary>
    public const float DualColumn1 = 104, DualColumn2 = 216;

    /// <summary>Half the arc's sweep in degrees for a reading at <paramref name="r"/> (0..1): 0 is a dot, 1 reaches ±72°.</summary>
    public static float DualSpan(float r) => Math.Clamp(r, 0, 1) * DualReach;

    /// <summary>The centres of an arc's two round caps, in normalised units, for an arc centred on <paramref name="centreDeg"/>.</summary>
    public static (PointF A, PointF B, float Radius) DualCaps(float centreDeg, float r)
    {
        float s = DualSpan(r);
        PointF At(float deg) => new(DualMid * MathF.Cos(Deg(deg)), DualMid * MathF.Sin(Deg(deg)));
        return (At(centreDeg - s), At(centreDeg + s), DualHalf);
    }

    /// <summary>The track ring's colour: CAM's <c>0.25·(0.75·lin(bg) + 0.03)</c>, scaled to land on the measured (21,21,21) over black.</summary>
    public static Color DualTrack(Color bg)
    {
        float black = ToSrgb(0.25f * 0.03f), scale = 21 / 255f / black;
        int Channel(byte c) => (int)MathF.Round(Math.Clamp(ToSrgb(0.25f * (0.75f * MathF.Pow(c / 255f, 2.2f) + 0.03f)) * scale, 0, 1) * 255);
        return Color.FromArgb(255, Channel(bg.R), Channel(bg.G), Channel(bg.B));
    }

    /// <summary>NZXT CAM's "Dual Infographic" in its Vertical layout: two readings, left and right, each with an arc that grows up and down from the middle.</summary>
    public static Bitmap RenderDual(LcdSettings s, float value1, float value2)
    {
        float r1 = Normalise(s.Reading1, value1), r2 = Normalise(s.Reading2, value2);
        var track = DualTrack(s.Background);
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            int* px = (int*)data.Scan0;
            int stride = data.Stride / 4;
            float s1 = float.IsNaN(r1) ? float.NaN : Deg(DualSpan(r1)), s2 = float.IsNaN(r2) ? float.NaN : Deg(DualSpan(r2));
            for (int py = 0; py < N; py++)
            {
                int* row = px + py * stride;
                for (int pxi = 0; pxi < N; pxi++)
                {
                    float x = (pxi + 0.5f - Half) / Half, y = (Half - (py + 0.5f)) / Half;
                    float rho = MathF.Sqrt(x * x + y * y);
                    Color c = s.Background;
                    float ring = Math.Clamp(0.5f - (MathF.Abs(rho - (TrackIn + TrackOut) / 2) - (TrackOut - TrackIn) / 2) * Half, 0, 1);
                    if (ring > 0) c = Blend(c, track, ring);
                    if (!float.IsNaN(s1)) c = Blend(c, s.Arc1, Math.Clamp(0.5f - (CentredArcDistance(x, y, MathF.PI, s1) - DualHalf) * Half, 0, 1));
                    if (!float.IsNaN(s2)) c = Blend(c, s.Arc2, Math.Clamp(0.5f - (CentredArcDistance(x, y, 0, s2) - DualHalf) * Half, 0, 1));
                    row[pxi] = c.ToArgb() | unchecked((int)0xFF000000);
                }
            }
        }
        bmp.UnlockBits(data);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (s.Logo) Brand.DrawMark(g, Half, 88, 36, s.Ink, Color.FromArgb(80, s.Ink), 9);
            using var family = new FontFamily("Bahnschrift");
            DualColumn(g, family, s.Reading1, value1, s.Number1, s.Label1, DualColumn1);
            DualColumn(g, family, s.Reading2, value2, s.Number2, s.Label2, DualColumn2);
        }
        return bmp;
    }

    /// <summary>Distance from (x, y) to an arc of radius <see cref="DualMid"/> spanning <paramref name="centre"/> ± <paramref name="half"/> (radians); a dot when the span is 0.</summary>
    static float CentredArcDistance(float x, float y, float centre, float half)
    {
        float d = MathF.Atan2(y, x) - centre;
        d -= 2 * MathF.PI * MathF.Floor((d + MathF.PI) / (2 * MathF.PI)); // into [-π, π)
        if (MathF.Abs(d) <= half) return MathF.Abs(MathF.Sqrt(x * x + y * y) - DualMid);
        float end = centre + (d > 0 ? half : -half);
        return Dist(x, y, DualMid * MathF.Cos(end), DualMid * MathF.Sin(end));
    }

    /// <summary>One reading's number, unit and label, centred on <paramref name="column"/>, as measured from CAM's Vertical layout.</summary>
    static void DualColumn(Graphics g, FontFamily family, LcdReading reading, float value, Color numberColour, Color labelColour, float column)
    {
        var (_, _, unit, label) = Range(reading);
        string number = float.IsNaN(value) ? "--" : ((int)MathF.Round(value)).ToString();
        const float baseline = 176, gap = 5;
        float digitHeight = 40, em = EmFor(family, FontStyle.Bold, "0", digitHeight);
        string unitText = float.IsNaN(value) ? "" : unit;

        // Shrink long numbers so they stay in their half: 8 px inside the arcs, clear of the centre line.
        bool left = column < Half;
        float minX = left ? 42 : Half + 4, maxX = left ? Half - 4 : N - 42;
        using (var probe = PathFor(family, FontStyle.Bold, number, em))
        using (var probeUnit = PathFor(family, FontStyle.Bold, unitText, em * 32 / 55))
        {
            float halfDigits = probe.GetBounds().Width / 2, unitW = unitText.Length > 0 ? probeUnit.GetBounds().Width + gap : 0;
            float fit = MathF.Min(1, MathF.Min((column - minX) / halfDigits, (maxX - column) / (halfDigits + unitW)));
            if (fit < 1) { em *= fit; digitHeight *= fit; }
        }

        using var ink = new SolidBrush(numberColour);
        using var numberPath = PathFor(family, FontStyle.Bold, number, em);
        var nb = numberPath.GetBounds();
        float top = baseline - digitHeight;
        Place(numberPath, column - (nb.X + nb.Width / 2), top - nb.Y);
        g.FillPath(ink, numberPath);
        var placed = numberPath.GetBounds();

        // Unit: about 32/55 of the number's size, hanging 5 px after the digits, 1 px below their top
        using var unitPath = PathFor(family, FontStyle.Bold, unitText, em * 32 / 55);
        var ub = unitPath.GetBounds();
        Place(unitPath, placed.Right + gap - ub.X, top + 1 - ub.Y);
        g.FillPath(ink, unitPath);

        // Label: capitals about 17.7 px tall, baseline at 221
        const float cap = 17.7f;
        float labelEm = EmFor(family, FontStyle.Bold, "L", cap);
        using var labelPath = PathFor(family, FontStyle.Bold, label, labelEm);
        var lb = labelPath.GetBounds();
        using var capPath = PathFor(family, FontStyle.Bold, "L", labelEm);
        var cb = capPath.GetBounds();
        using var labelInk = new SolidBrush(labelColour);
        Place(labelPath, column - (lb.X + lb.Width / 2), 221 - cap - cb.Y);
        g.FillPath(labelInk, labelPath);
    }

    // ------------------------------------------------------------------ rotation

    /// <summary>
    /// How far a live frame is turned clockwise before upload for a dial at <paramref name="dial"/>: the dial says how
    /// far the pump head is turned clockwise, so the picture is turned back by as much.
    /// </summary>
    public static int RawClockwise(int dial) => (360 - (dial % 360 + 360) % 360) % 360;

    /// <summary>How far a GIF is turned clockwise before upload: the cooler shows bucket GIFs 90° counter-clockwise of live frames.</summary>
    public static int GifClockwise(int dial) => (RawClockwise(dial) + 90) % 360;

    /// <summary>
    /// Turns a square picture of 32-bit pixels clockwise about its centre: exactly for multiples of 90°, bilinear
    /// otherwise. Pixels from outside the picture come out opaque black (the corners are off the round glass anyway).
    /// </summary>
    public static void Rotate(ReadOnlySpan<uint> src, Span<uint> dst, int size, int clockwise)
    {
        int turn = (clockwise % 360 + 360) % 360, n = size - 1;
        switch (turn)
        {
            case 0: src.CopyTo(dst); return;
            case 90: for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) dst[y * size + x] = src[(n - x) * size + y]; return;
            case 180: for (int i = 0; i < size * size; i++) dst[i] = src[size * size - 1 - i]; return;
            case 270: for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) dst[y * size + x] = src[x * size + (n - y)]; return;
        }
        float t = turn * MathF.PI / 180, cos = MathF.Cos(t), sin = MathF.Sin(t), c = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Screen y points down, so this inverse of a clockwise turn finds where each pixel came from.
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                float sx = dx * cos + dy * sin + c - 0.5f, sy = -dx * sin + dy * cos + c - 0.5f;
                dst[y * size + x] = Bilinear(src, size, sx, sy);
            }
    }

    static uint Bilinear(ReadOnlySpan<uint> src, int size, float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        uint a = At(src, size, x0, y0), b = At(src, size, x0 + 1, y0), c = At(src, size, x0, y0 + 1), d = At(src, size, x0 + 1, y0 + 1);
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            float top = ((a >> shift) & 0xFF) * (1 - fx) + ((b >> shift) & 0xFF) * fx;
            float bottom = ((c >> shift) & 0xFF) * (1 - fx) + ((d >> shift) & 0xFF) * fx;
            result |= (uint)Math.Clamp((int)MathF.Round(top * (1 - fy) + bottom * fy), 0, 255) << shift;
        }
        return result;
    }

    static uint At(ReadOnlySpan<uint> src, int size, int x, int y) => x >= 0 && y >= 0 && x < size && y < size ? src[y * size + x] : 0xFF000000;

    // ------------------------------------------------------------------ upload format

    /// <summary>The frame as the cooler takes it: RGBA, row by row from the top left, turned for the dial (see <see cref="RawClockwise"/>).</summary>
    public static byte[] ToRgba(Bitmap frame, int dial)
    {
        var pixels = new uint[N * N];
        var data = frame.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < N; y++) new ReadOnlySpan<uint>((byte*)data.Scan0 + y * data.Stride, N).CopyTo(pixels.AsSpan(y * N, N));
        }
        frame.UnlockBits(data);
        var turned = new uint[N * N];
        Rotate(pixels, turned, N, RawClockwise(dial));
        var rgba = new byte[KrakenSession.FrameBytes];
        for (int i = 0; i < turned.Length; i++)
        {
            uint p = turned[i]; // A R G B from the top byte down
            rgba[i * 4] = (byte)(p >> 16);
            rgba[i * 4 + 1] = (byte)(p >> 8);
            rgba[i * 4 + 2] = (byte)p;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }
}
