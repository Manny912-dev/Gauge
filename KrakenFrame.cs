using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Gauge;

/// <summary>
/// Draws the Kraken Z3's 320×320 screen in the "Single Infographic" layout: a 270° gradient ring with a dot at
/// the reading ("Radial Fill") or a full-screen gradient ("Full Fill"), with the number, its unit and a label.
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

    // ------------------------------------------------------------------ upload format

    /// <summary>The frame as the cooler takes it: RGBA, row by row from the top left, rotated clockwise by <paramref name="rotation"/>.</summary>
    public static byte[] ToRgba(Bitmap frame, int rotation)
    {
        using var turned = (Bitmap)frame.Clone();
        turned.RotateFlip(rotation switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        var data = turned.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rgba = new byte[KrakenSession.FrameBytes];
        unsafe
        {
            for (int y = 0; y < N; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride; // B, G, R, A
                for (int x = 0; x < N; x++)
                {
                    int o = (y * N + x) * 4;
                    rgba[o] = row[x * 4 + 2];
                    rgba[o + 1] = row[x * 4 + 1];
                    rgba[o + 2] = row[x * 4];
                    rgba[o + 3] = 255;
                }
            }
        }
        turned.UnlockBits(data);
        return rgba;
    }
}
