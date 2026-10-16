using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Gauge;

/// <summary>Draws the whole window, title bar and navigation included, onto one surface. No child controls.</summary>
internal sealed partial class Renderer : IDisposable
{
    // Hit ids. Anything below HitRow is clickable; HitLast closes the range that shows a hand cursor.
    public const int HitMonitor = 1, HitSpecs = 2, HitElevate = 3, HitCopy = 4, HitGpu = 5, HitMin = 6, HitMax = 7, HitClose = 8, HitSettings = 9, HitSort = 10, HitUpdate = 17,
        HitSwatch = 100, HitHue = 120, HitReset = 121, HitAutoUpdate = 122, HitAutoStart = 123, HitTray = 124, HitLast = 199, HitRow = 200;
    public const int PageMonitor = 0, PageSpecs = 1, PageLighting = 2, PageSettings = 3;
    /// <summary>The header strip, which is also the title bar.</summary>
    public const float ChromeHeight = 56;
    /// <summary>The navigation rail down the left edge.</summary>
    public const float RailWidth = 76;

    // Surfaces, from the window up. One flat plate style; depth comes from a faint vertical shade and a hairline edge.
    static readonly Color Bg = Rgb(0x0B0C0E), RailBg = Rgb(0x08090B), PlateTop = Rgb(0x15161A), PlateBottom = Rgb(0x111215), Well = Rgb(0x0D0E11), Raised = Rgb(0x26292F);
    // Text: Ink for content, Soft for secondary figures, Muted for labels, Faint for the least important (still 4.5:1 on a plate).
    static readonly Color Ink = Rgb(0xF2F3F5), Soft = Rgb(0xC9CDD3), Muted = Rgb(0x9AA0A9), Faint = Rgb(0x7A808A), Second = Rgb(0x8E96A3);
    static readonly Color Warn = Rgb(0xFFA53C), Hot = Rgb(0xFF5A4D), Close = Rgb(0xE5484D);

    // The accent marks live activity and the current choice; capacity and everything static stay neutral.
    const float HueSaturation = 0.76f;
    public static readonly Color DefaultAccent = Rgb(0xFFA53C);
    public static readonly Color[] Presets = [DefaultAccent, Rgb(0xFF6B5E), Rgb(0xFF5C93), Rgb(0xA78BFA), Rgb(0x5B9DFF), Rgb(0x3CCFE0), Rgb(0x4ADE9B), Rgb(0xB8E04A)];
    public static Color Accent { get; private set; } = DefaultAccent;
    public static Color Background => Bg;

    static Color Rgb(int v) => Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
    static Color White(int alpha) => Color.FromArgb(alpha, 255, 255, 255);

    readonly record struct Tile(string Label, string Value, string Unit, float Fraction = float.NaN, int Kind = 0); // kind 2: a temperature, coloured by how hot it is

    int _tab;
    /// <summary>The page shown. Changing it starts the new page at the top.</summary>
    public int Tab
    {
        get => _tab;
        set { if (_tab != value) { _tab = value; Scroll = 0; } }
    }
    public bool Maximized;
    /// <summary>Windows 11 rounds the window and draws its outline, so the square one here is left out.</summary>
    public bool SystemFrame;
    public PointF Mouse = new(-1, -1);
    public float Scroll;
    public List<SpecSection>? Specs;
    public SpecSummary? Summary;
    public long CopiedUntil;
    /// <summary>Shown as the settings switches; MainForm owns the real state.</summary>
    public bool StartWithWindows, KeepInTray;
    public Updater? Updates;
    /// <summary>Shown briefly in the header on the first run after an update.</summary>
    public string? UpdatedTo;
    public long UpdatedNoticeUntil;
    public RectangleF HueRect;
    public readonly List<(RectangleF Rect, int Id)> Hits = new();
    public float MaxScroll { get; private set; }

    readonly Telemetry _t;
    readonly Dictionary<int, SolidBrush> _brushes = new();
    readonly List<Tile> _tiles = new();
    readonly List<(string Label, string Value, string Unit, string Note)> _figures = new();
    readonly StringFormat _sf, _sfEllipsis, _sfWrap;
    float _s;
    Font _fCaps = null!, _fTiny = null!, _fLabel = null!, _fBody = null!, _fStrong = null!, _fNum = null!, _fVal = null!, _fUnit = null!;
    Font _fHero = null!, _fHeroUnit = null!, _fMid = null!, _fTitle = null!, _fPage = null!, _fRail = null!, _fSection = null!;

    public Renderer(Telemetry telemetry, float scale)
    {
        _t = telemetry;
        _sf = new StringFormat(StringFormat.GenericTypographic);
        _sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        _sfEllipsis = new StringFormat(_sf) { Trimming = StringTrimming.EllipsisCharacter };
        _sfWrap = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
        SetScale(scale);
    }

    public void SetScale(float scale)
    {
        DisposeFonts();
        _s = scale;
        // One type scale. Bahnschrift (DIN) carries numbers, page titles and the few eyebrow labels;
        // Segoe UI carries names, labels and prose.
        _fCaps = Make("Bahnschrift SemiBold", 10f);
        _fTiny = Make("Bahnschrift", 9.5f);
        _fLabel = Make("Segoe UI", 12f);
        _fBody = Make("Segoe UI", 13f);
        _fStrong = Make("Segoe UI Semibold", 13f);
        _fSection = Make("Segoe UI Semibold", 15.5f);
        _fRail = Make("Segoe UI Semibold", 10.5f);
        _fNum = Make("Bahnschrift SemiLight", 13f);
        _fUnit = Make("Bahnschrift SemiLight", 11.5f);
        _fVal = Make("Bahnschrift SemiLight", 20f);
        _fMid = Make("Bahnschrift Light", 30f);
        _fTitle = Make("Bahnschrift Light", 28f);
        _fPage = Make("Bahnschrift SemiBold", 19f);
        _fHero = Make("Bahnschrift Light", 56f);
        _fHeroUnit = Make("Bahnschrift Light", 22f);
    }

    Font Make(string family, float px) => new(family, px * _s, FontStyle.Regular, GraphicsUnit.Pixel);

    public int HitAt(PointF p)
    {
        for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].Rect.Contains(p)) return Hits[i].Id;
        return 0;
    }

    // ---------------------------------------------------------------- primitives

    SolidBrush Brush(Color c)
    {
        if (!_brushes.TryGetValue(c.ToArgb(), out var b)) _brushes[c.ToArgb()] = b = new SolidBrush(c);
        return b;
    }

    float Width(Graphics g, string s, Font f) => s.Length == 0 ? 0 : g.MeasureString(s, f, PointF.Empty, _sf).Width;

    static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    /// <summary>Draws text with its baseline at y. Returns the x after the text.</summary>
    float Text(Graphics g, string s, Font f, Color c, float x, float baseline)
    {
        g.DrawString(s, f, Brush(c), x, baseline - Ascent(f), _sf);
        return x + Width(g, s, f);
    }

    void TextRight(Graphics g, string s, Font f, Color c, float right, float baseline)
        => g.DrawString(s, f, Brush(c), right - Width(g, s, f), baseline - Ascent(f), _sf);

    void TextCenter(Graphics g, string s, Font f, Color c, float centre, float baseline)
        => g.DrawString(s, f, Brush(c), centre - Width(g, s, f) / 2, baseline - Ascent(f), _sf);

    void TextClipped(Graphics g, string s, Font f, Color c, float x, float baseline, float maxWidth)
    {
        if (maxWidth < 8 * _s) return;
        g.DrawString(s, f, Brush(c), new RectangleF(x, baseline - Ascent(f), maxWidth, f.GetHeight(g) + 2), _sfEllipsis);
    }

    /// <summary>Right-aligned text that truncates with an ellipsis when it would cross <paramref name="left"/>.</summary>
    void TextRightClipped(Graphics g, string s, Font f, Color c, float left, float right, float baseline)
    {
        float w = Width(g, s, f);
        if (w <= right - left) Text(g, s, f, c, right - w, baseline);
        else TextClipped(g, s, f, c, left, baseline, right - left);
    }

    /// <summary>Wrapping text in a column, at most <paramref name="lines"/> lines. Returns the bottom of what was drawn.</summary>
    float Paragraph(Graphics g, string s, Font f, Color c, float x, float top, float width, int lines = 3)
    {
        if (width < 20 * _s || s.Length == 0) return top;
        float lineH = f.GetHeight(g);
        var size = g.MeasureString(s, f, new SizeF(width, lineH * lines + 1), _sfWrap);
        g.DrawString(s, f, Brush(c), new RectangleF(x, top, width, MathF.Min(size.Height, lineH * lines) + 1), _sfWrap);
        return top + MathF.Min(size.Height, lineH * lines);
    }

    /// <summary>A number with a quieter unit after it. Returns the x after the unit.</summary>
    float ValueUnit(Graphics g, string value, string unit, Font fv, Font fu, float x, float baseline, Color c)
    {
        float end = Text(g, value, fv, c, x, baseline);
        return unit.Length == 0 ? end : Text(g, unit, fu, Muted, end + UnitGap(unit), baseline);
    }

    float UnitGap(string unit) => (unit is "%" or "°C" ? 1.5f : 3.5f) * _s;

    void ValueUnitRight(Graphics g, string value, string unit, Font fv, Font fu, float right, float baseline, Color c)
    {
        float w = Width(g, value, fv) + (unit.Length == 0 ? 0 : UnitGap(unit) + Width(g, unit, fu));
        ValueUnit(g, value, unit, fv, fu, right - w, baseline, c);
    }

    const float Tracking = 1.5f;

    /// <summary>Small tracked capitals: the eyebrow over each block, and nowhere else.</summary>
    float Caps(Graphics g, string s, float x, float baseline, Color c)
    {
        Span<char> one = stackalloc char[1];
        foreach (char ch in s)
        {
            one[0] = char.ToUpperInvariant(ch);
            string glyph = one.ToString();
            g.DrawString(glyph, _fCaps, Brush(c), x, baseline - Ascent(_fCaps), _sf);
            x += Width(g, glyph, _fCaps) + Tracking * _s;
        }
        return x - Tracking * _s;
    }

    float CapsWidth(Graphics g, string s) => Width(g, s.ToUpperInvariant(), _fCaps) + (s.Length - 1) * Tracking * _s;

    void CapsRight(Graphics g, string s, float right, float baseline, Color c) => Caps(g, s, right - CapsWidth(g, s), baseline, c);

    static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = MathF.Min(radius * 2, MathF.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static RectangleF Snap(RectangleF r)
        => new(MathF.Round(r.X) + 0.5f, MathF.Round(r.Y) + 0.5f, MathF.Round(r.Width) - 1, MathF.Round(r.Height) - 1);

    void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color border = default)
    {
        using var path = Round(Snap(r), radius);
        if (fill.A > 0) g.FillPath(Brush(fill), path);
        if (border.A > 0) { using var pen = new Pen(border, 1); g.DrawPath(pen, path); }
    }

    /// <summary>A filled rounded shape without pixel snapping, for small bars and pills.</summary>
    void Pill(Graphics g, RectangleF r, Color fill)
    {
        using var path = Round(r, MathF.Min(r.Height, r.Width) / 2);
        g.FillPath(Brush(fill), path);
    }

    const float PlateRadius = 12;

    /// <summary>The one container: a faint vertical shade and a hairline edge.</summary>
    void Plate(Graphics g, RectangleF r)
    {
        var rect = Snap(r);
        using var path = Round(rect, PlateRadius * _s);
        using (var shade = new LinearGradientBrush(new PointF(0, rect.Y - 1), new PointF(0, rect.Bottom + 1), PlateTop, PlateBottom))
            g.FillPath(shade, path);
        PlateEdge(g, r);
    }

    void PlateEdge(Graphics g, RectangleF r)
    {
        using var path = Round(Snap(r), PlateRadius * _s);
        using var edge = new Pen(White(13), 1);
        g.DrawPath(edge, path);
    }

    void Rect(Graphics g, Color c, float x, float y, float w, float h)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.FillRectangle(Brush(c), x, y, w, h);
        g.SmoothingMode = mode;
    }

    void Hairline(Graphics g, float x1, float x2, float y, int alpha = 12)
        => Rect(g, White(alpha), MathF.Round(x1), MathF.Round(y), MathF.Round(x2 - x1), 1);

    void Divider(Graphics g, float x, float y1, float y2)
        => Rect(g, White(12), MathF.Round(x), MathF.Round(y1), 1, MathF.Round(y2 - y1));

    /// <summary>The single bar style: a rounded track with a rounded fill.</summary>
    void Bar(Graphics g, float x, float y, float width, float fraction, Color c, float height = 4)
    {
        float h = MathF.Max(2, MathF.Round(height * _s));
        var track = new RectangleF(MathF.Round(x), MathF.Round(y), MathF.Round(width), h);
        Pill(g, track, White(14));
        if (!float.IsNaN(fraction) && fraction > 0)
            Pill(g, new RectangleF(track.X, track.Y, MathF.Max(h, track.Width * Math.Clamp(fraction, 0, 1)), h), c);
    }

    /// <summary>Capacity colour: neutral until it gets full.</summary>
    static Color Fullness(float fraction) => fraction >= 0.92f ? Hot : fraction >= 0.82f ? Warn : Soft;

    PointF[]? Curve(RectangleF plot, Ring ring, float max, float baseY, float amplitude)
    {
        int n = ring.Count;
        if (n < 2 || max <= 0 || plot.Width < 8) return null;
        float step = plot.Width / (ring.Capacity - 1);
        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
            pts[i] = new PointF(plot.Right - (n - 1 - i) * step, baseY - Math.Clamp(ring.At(i) / max, 0, 1) * amplitude);
        return pts;
    }

    /// <summary>Filled time series growing from baseY (amplitude may be negative to grow downward).</summary>
    void Area(Graphics g, RectangleF plot, Ring ring, float max, Color c, float baseY, float amplitude, bool marker, int fillAlpha = 72)
    {
        var pts = Curve(plot, ring, max, baseY, amplitude);
        if (pts == null) return;
        const float tension = 0.28f;
        var state = g.Save();
        // Keep spline overshoot from crossing the baseline.
        g.SetClip(amplitude > 0
            ? RectangleF.FromLTRB(plot.X, baseY - amplitude - 3 * _s, plot.Right + 8 * _s, baseY + 1)
            : RectangleF.FromLTRB(plot.X, baseY, plot.Right + 8 * _s, baseY - amplitude + 3 * _s), CombineMode.Intersect);
        using (var path = new GraphicsPath())
        {
            path.AddCurve(pts, tension);
            path.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, baseY);
            path.AddLine(pts[^1].X, baseY, pts[0].X, baseY);
            path.CloseFigure();
            using var fill = new LinearGradientBrush(new PointF(0, baseY - amplitude * 1.02f), new PointF(0, baseY + MathF.Sign(amplitude)),
                Color.FromArgb(fillAlpha, c), Color.FromArgb(0, c)) { WrapMode = WrapMode.TileFlipXY };
            g.FillPath(fill, path);
        }
        using (var pen = new Pen(c, 1.7f * _s) { LineJoin = LineJoin.Round })
            g.DrawCurve(pen, pts, tension);
        if (marker)
        {
            var p = pts[^1];
            float halo = 6f * _s, dot = 2.6f * _s;
            g.FillEllipse(Brush(Color.FromArgb(46, c)), p.X - halo, p.Y - halo, halo * 2, halo * 2);
            g.FillEllipse(Brush(c), p.X - dot, p.Y - dot, dot * 2, dot * 2);
        }
        g.Restore(state);
    }

    void DottedLine(Graphics g, float x1, float x2, float y, int alpha = 22)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(White(alpha), 1) { DashPattern = [1f, 3f] };
        g.DrawLine(pen, MathF.Round(x1), MathF.Round(y), MathF.Round(x2), MathF.Round(y));
        g.SmoothingMode = mode;
    }

    void Arrow(Graphics g, float cx, float cy, bool down, Color c)
    {
        float h = 5 * _s, w = 3.6f * _s, dir = down ? 1 : -1;
        using var pen = new Pen(c, 1.6f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, cx, cy - h * dir, cx, cy + h * dir);
        g.DrawLines(pen, [new PointF(cx - w, cy + (h - w) * dir), new PointF(cx, cy + h * dir), new PointF(cx + w, cy + (h - w) * dir)]);
    }

    /// <summary>The brand mark (see <see cref="Brand"/>), lit in the user's accent, in a square box <paramref name="size"/> wide.</summary>
    void Mark(Graphics g, float cx, float cy, float size) => Brand.DrawMark(g, cx, cy, size, Accent, White(60), 8.5f);

    bool Hover(RectangleF r) => r.Contains(Mouse);

    /// <summary>A quiet button sized to its label, right-aligned at <paramref name="right"/>.</summary>
    float Button(Graphics g, string label, int id, float right, float mid, Color dot)
    {
        float pad = 13 * _s, h = 30 * _s, lead = dot.A > 0 ? 13 * _s : 0;
        float width = Width(g, label, _fBody) + pad * 2 + lead;
        var r = new RectangleF(right - width, mid - h / 2, width, h);
        if (id != 0) Hits.Add((r, id));
        bool hot = id != 0 && Hover(r);
        FillRound(g, r, 8 * _s, hot ? White(16) : White(7), White(hot ? 44 : 24));
        if (dot.A > 0)
        {
            float d = 2.8f * _s;
            g.FillEllipse(Brush(dot), r.X + pad - 1 * _s, mid - d + 0.5f * _s, d * 2, d * 2);
        }
        Text(g, label, _fBody, hot ? Ink : Soft, r.X + pad + lead, mid + 4.5f * _s);
        return r.X;
    }

    /// <summary>An on/off switch with its right edge at <paramref name="right"/>, centred on <paramref name="y"/>.</summary>
    void SwitchAt(Graphics g, bool on, float right, float y)
    {
        float tw = 36 * _s, th = 20 * _s, knob = th - 6 * _s;
        var track = new RectangleF(right - tw, y - th / 2, tw, th);
        Pill(g, track, on ? Accent : White(26));
        float kx = on ? track.Right - knob - 3 * _s : track.X + 3 * _s;
        g.FillEllipse(Brush(on ? Rgb(0x15171A) : Soft), kx, y - knob / 2, knob, knob);
    }

    // ---------------------------------------------------------------- frame

    public void Render(Graphics g, float w, float h)
    {
        float scroll = Scroll;
        RenderOnce(g, w, h);
        // The page turned out shorter than the scroll position (the window grew, or content went away):
        // draw it again where it now sits rather than showing an empty frame.
        if (Scroll != scroll) RenderOnce(g, w, h);
    }

    void RenderOnce(Graphics g, float w, float h)
    {
        g.Clear(Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Hits.Clear();

        // The page first; the rail and header after, so their targets win over anything scrolled beneath them.
        float m = 20 * _s;
        var area = RectangleF.FromLTRB(RailWidth * _s + m, ChromeHeight * _s + 2 * _s, w - m, h - 18 * _s);
        switch (Tab)
        {
            case PageMonitor: RenderMonitor(g, area); break;
            case PageSpecs: RenderSpecs(g, area); break;
            case PageLighting: RenderLighting(g, area); break;
            default: RenderSettings(g, area); break;
        }
        Hits.Add((new RectangleF(0, 0, RailWidth * _s, h), 0));
        Hits.Add((new RectangleF(0, 0, w, ChromeHeight * _s), 0));
        Rail(g, h);
        Header(g, w);

        if (!Maximized && !SystemFrame)
        {
            using var frame = new Pen(White(26), 1);
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawRectangle(frame, 0, 0, w - 1, h - 1);
            g.SmoothingMode = mode;
        }
    }

    void Rail(Graphics g, float h)
    {
        float rw = RailWidth * _s;
        Rect(g, RailBg, 0, 0, rw, h);
        Rect(g, White(9), MathF.Round(rw) - 1, 0, 1, h);
        Mark(g, rw / 2, ChromeHeight * _s / 2, 28 * _s);

        float y = 68 * _s, pitch = 62 * _s;
        NavItem(g, "Monitor", HitMonitor, PageMonitor, y);
        NavItem(g, "Specs", HitSpecs, PageSpecs, y + pitch);
        NavItem(g, "Lighting", HitLighting, PageLighting, y + pitch * 2);
        NavItem(g, "Settings", HitSettings, PageSettings, MathF.Max(y + pitch * 3 + 12 * _s, h - 72 * _s));
    }

    void NavItem(Graphics g, string label, int id, int page, float y)
    {
        float rw = RailWidth * _s, inset = 9 * _s;
        var r = new RectangleF(inset, y, rw - inset * 2, 56 * _s);
        Hits.Add((r, id));
        bool on = Tab == page, hot = Hover(r);
        if (on || hot) FillRound(g, r, 10 * _s, White(on ? 10 : 5));
        if (on) Pill(g, new RectangleF(-2 * _s, r.Y + 17 * _s, 5 * _s, 22 * _s), Accent);
        float cx = rw / 2;
        Icon(g, label, cx - 9 * _s, r.Y + 9 * _s, on ? Accent : hot ? Ink : Muted, 18);
        TextCenter(g, label, _fRail, on || hot ? Ink : Muted, cx, r.Y + 45 * _s);
        // A waiting update is worth a glance from any page.
        if (id == HitSettings && Updates is { Status: UpdateStatus.Ready })
        {
            float d = 3.5f * _s;
            g.FillEllipse(Brush(Accent), cx + 7 * _s, r.Y + 7 * _s, d * 2, d * 2);
        }
    }

    void Header(Graphics g, float w)
    {
        float ch = ChromeHeight * _s, mid = ch / 2, x = RailWidth * _s + 20 * _s;
        string title = Tab switch { PageMonitor => "Monitor", PageSpecs => "Specs", PageLighting => "Lighting", _ => "Settings" };
        float end = Text(g, title, _fPage, Ink, x, mid + 7 * _s);
        if (Tab == PageMonitor)
        {
            float dot = 2.8f * _s, dx = end + 16 * _s;
            g.FillEllipse(Brush(Accent), dx, mid + 1 * _s - dot, dot * 2, dot * 2);
            Text(g, "Live", _fLabel, Faint, dx + 11 * _s, mid + 5.5f * _s);
        }
        else if (Tab == PageLighting && Lights is { Scanned: true } l)
        {
            int n;
            lock (l.Sync) n = l.Devices.Count;
            Text(g, n == 1 ? "1 device" : $"{n} devices", _fLabel, Faint, end + 14 * _s, mid + 5.5f * _s);
        }

        // Window controls
        float bw = 46 * _s, bh = 32 * _s, bx = w - 6 * _s - bw * 3, by = mid - bh / 2;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(bx + i * bw, by, bw, bh);
            int id = HitMin + i;
            Hits.Add((r, id));
            bool hot = Hover(r);
            if (hot) FillRound(g, r, 7 * _s, id == HitClose ? Close : White(18));
            float cx = MathF.Round(r.X + r.Width / 2), cy = MathF.Round(r.Y + r.Height / 2), k = MathF.Round(5 * _s);
            using var pen = new Pen(hot ? Ink : Muted, MathF.Max(1, MathF.Round(1.1f * _s)));
            var mode = g.SmoothingMode;
            if (id != HitClose) g.SmoothingMode = SmoothingMode.None;
            if (id == HitMin) g.DrawLine(pen, cx - k, cy, cx + k, cy);
            else if (id == HitMax && !Maximized) g.DrawRectangle(pen, cx - k, cy - k, k * 2, k * 2);
            else if (id == HitMax)
            {
                float o = MathF.Round(2 * _s);
                g.DrawRectangle(pen, cx - k, cy - k + o, k * 2 - o, k * 2 - o);
                g.DrawLines(pen, [new PointF(cx - k + o, cy - k + o), new PointF(cx - k + o, cy - k), new PointF(cx + k, cy - k), new PointF(cx + k, cy + k - o), new PointF(cx + k - o, cy + k - o)]);
            }
            else
            {
                g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
            }
            g.SmoothingMode = mode;
        }

        // The page's action, with a finished update taking priority everywhere
        float right = bx - 14 * _s;
        if (UpdatedTo != null && Environment.TickCount64 < UpdatedNoticeUntil) right = Button(g, $"Updated to {UpdatedTo}", 0, right, mid, Accent) - 8 * _s;
        else if (Updates is { Status: UpdateStatus.Ready }) right = Button(g, "Update ready  ·  Restart", HitUpdate, right, mid, Accent) - 8 * _s;
        if (Tab == PageSpecs && Specs != null) Button(g, Environment.TickCount64 < CopiedUntil ? "Copied" : "Copy specs", HitCopy, right, mid, default);
        else if (Tab == PageLighting && Lights is { Scanned: true }) Button(g, "Rescan", HitRescan, right, mid, default);
    }

    // ---------------------------------------------------------------- monitor

    void RenderMonitor(Graphics g, RectangleF a)
    {
        float gap = 12 * _s, half = (a.Width - gap) / 2;
        float aH = Math.Clamp(a.Height * 0.44f, 214 * _s, 300 * _s), bH = a.Height < 560 * _s ? 104 * _s : 112 * _s;

        // Processor
        _tiles.Clear();
        if (!float.IsNaN(_t.CpuTemp)) _tiles.Add(new("Temperature", _t.CpuTemp.ToString("0"), "°C", _t.CpuTemp, 2));
        if (!float.IsNaN(_t.CpuClock)) _tiles.Add(new("Clock", (_t.CpuClock / 1000).ToString("0.00"), "GHz"));
        if (!float.IsNaN(_t.CpuFan)) _tiles.Add(new("Fan", _t.CpuFan.ToString("N0"), "RPM"));
        if (!float.IsNaN(_t.CpuPower)) _tiles.Add(new("Power", _t.CpuPower.ToString("0.0"), "W"));
        if (_tiles.Count < 4) _tiles.Add(new("Processes", _t.Processes.ProcessCount.ToString("N0"), ""));
        if (_tiles.Count < 4) _tiles.Add(new("Threads", _t.Processes.ThreadCount.ToString("N0"), ""));
        Unit(g, new RectangleF(a.X, a.Y, half, aH), "Processor", _t.CpuName, _t.CpuLoad, _t.CpuHistory, null);

        // Graphics
        _tiles.Clear();
        var gpu = _t.Gpus.Count > 0 ? _t.Gpus[Math.Min(_t.GpuIndex, _t.Gpus.Count - 1)] : null;
        if (gpu != null)
        {
            if (!float.IsNaN(gpu.Temp)) _tiles.Add(new("Temperature", gpu.Temp.ToString("0"), "°C", gpu.Temp, 2));
            if (!float.IsNaN(gpu.Clock)) _tiles.Add(new("Clock", gpu.Clock.ToString("N0"), "MHz"));
            if (!float.IsNaN(gpu.VramUsed) && !float.IsNaN(gpu.VramTotal))
                _tiles.Add(new("Memory", (gpu.VramUsed / 1024).ToString("0.0"), $"/ {gpu.VramTotal / 1024:0.#} GB"));
            if (!float.IsNaN(gpu.Fan)) _tiles.Add(gpu.FanIsPercent ? new("Fan", gpu.Fan.ToString("0"), "%") : new("Fan", gpu.Fan.ToString("N0"), "RPM"));
            else if (!float.IsNaN(gpu.Power)) _tiles.Add(new("Power", gpu.Power.ToString("0.0"), "W"));
        }
        string? pager = _t.Gpus.Count > 1 ? $"{_t.GpuIndex + 1} / {_t.Gpus.Count}" : null;
        Unit(g, new RectangleF(a.X + half + gap, a.Y, half, aH), "Graphics", gpu?.Name ?? (_t.HardwareReady ? "No graphics sensors found" : "Detecting…"), gpu?.Load ?? float.NaN, gpu?.History, pager);

        var vitals = new RectangleF(a.X, a.Y + aH + gap, a.Width, bH);
        Vitals(g, vitals);

        float pTop = vitals.Bottom + gap;
        if (a.Bottom - pTop > 92 * _s) ProcessPanel(g, RectangleF.FromLTRB(a.X, pTop, a.Right, a.Bottom));
    }

    /// <summary>A processor or graphics card: the reading, its history edge to edge, and up to four details.</summary>
    void Unit(Graphics g, RectangleF r, string title, string name, float load, Ring? history, string? pager)
    {
        Plate(g, r);
        float p = 20 * _s, x = r.X + p, right = r.Right - p, ey = r.Y + 30 * _s;

        float labelEnd = Caps(g, title, x, ey, Muted);
        float nameRight = right;
        if (pager != null)
        {
            float pw = Width(g, pager, _fUnit) + 16 * _s;
            var chip = new RectangleF(right - pw, ey - 14.5f * _s, pw, 21 * _s);
            Hits.Add((chip, HitGpu));
            bool hot = Hover(chip);
            FillRound(g, chip, 6 * _s, hot ? White(18) : White(7), White(hot ? 40 : 22));
            Text(g, pager, _fUnit, hot ? Ink : Muted, chip.X + 8 * _s, ey - 0.5f * _s);
            nameRight = chip.X - 10 * _s;
        }
        TextRightClipped(g, name, _fBody, Muted, labelEnd + 16 * _s, nameRight, ey);

        // Headline reading, with its recent range underneath
        float hb = r.Y + 96 * _s;
        string value = float.IsNaN(load) ? "–" : MathF.Round(load).ToString("0");
        float after = Text(g, value, _fHero, Ink, x - 3 * _s, hb);
        Text(g, "%", _fHeroUnit, Muted, after + 4 * _s, hb);
        if (history != null && history.Count > 1)
        {
            float sum = 0;
            for (int i = 0; i < history.Count; i++) sum += history.At(i);
            float sb = hb + 24 * _s;
            float sx = Text(g, "Average ", _fLabel, Faint, x, sb);
            sx = Text(g, $"{MathF.Round(sum / history.Count):0}%", _fLabel, Soft, sx, sb);
            sx = Text(g, "     Peak ", _fLabel, Faint, sx, sb);
            Text(g, $"{MathF.Round(history.Max()):0}%", _fLabel, Soft, sx, sb);
        }

        // Details, two by two, opposite the reading
        float colW = Math.Clamp((r.Width - 2 * p - 190 * _s) / 2, 86 * _s, 118 * _s), block = right - 2 * colW + 14 * _s;
        for (int i = 0; i < _tiles.Count && i < 4; i++)
        {
            var t = _tiles[i];
            float tx = block + (i % 2) * colW, lb = r.Y + 62 * _s + (i / 2) * 48 * _s;
            Text(g, t.Label, _fLabel, Muted, tx, lb);
            Color c = t.Kind == 2 && t.Fraction >= 90 ? Hot : t.Kind == 2 && t.Fraction >= 80 ? Warn : Ink;
            ValueUnit(g, t.Value, t.Unit, _fVal, _fUnit, tx - 1 * _s, lb + 23 * _s, c);
        }

        // Ninety seconds of history, edge to edge along the bottom of the plate
        var plot = RectangleF.FromLTRB(r.X, r.Y + 146 * _s, right - 2 * _s, r.Bottom - 1);
        if (plot.Height > 24 * _s)
        {
            var state = g.Save();
            using (var clip = Round(Snap(r), PlateRadius * _s)) g.SetClip(clip, CombineMode.Intersect);
            float amp = plot.Height - 14 * _s;
            DottedLine(g, r.X + 1, r.Right - 1, plot.Bottom - amp / 2, 18);
            DottedLine(g, r.X + 1, r.Right - 1, plot.Bottom - amp, 12);
            if (history != null) Area(g, plot, history, 100, Accent, plot.Bottom, amp, true);
            g.Restore(state);
            PlateEdge(g, r);
        }
    }

    /// <summary>Memory, network and storage: one plate divided in three.</summary>
    void Vitals(Graphics g, RectangleF r)
    {
        Plate(g, r);
        float side = MathF.Round(r.Width * 0.29f);
        var mem = new RectangleF(r.X, r.Y, side, r.Height);
        var net = new RectangleF(mem.Right, r.Y, r.Width - 2 * side, r.Height);
        var sto = new RectangleF(net.Right, r.Y, side, r.Height);
        Divider(g, net.X, r.Y + 18 * _s, r.Bottom - 18 * _s);
        Divider(g, sto.X, r.Y + 18 * _s, r.Bottom - 18 * _s);
        MemoryCell(g, mem);
        NetworkCell(g, net);
        StorageCell(g, sto);
    }

    void MemoryCell(Graphics g, RectangleF r)
    {
        float p = 20 * _s, x = r.X + p, right = r.Right - p, ey = r.Y + 30 * _s;
        Caps(g, "Memory", x, ey, Muted);
        ValueUnitRight(g, (_t.MemUsed / 1073741824.0).ToString("0.0"), $"/ {_t.MemTotal / 1073741824.0:0.0} GB", _fNum, _fUnit, right, ey, Soft);
        float after = Text(g, MathF.Round(_t.MemLoad).ToString("0"), _fMid, Ink, x - 2 * _s, r.Y + 74 * _s);
        Text(g, "%", _fUnit, Muted, after + 3 * _s, r.Y + 74 * _s);
        Bar(g, x, r.Bottom - 24 * _s, right - x, _t.MemLoad / 100, Fullness(_t.MemLoad / 100));
    }

    void NetworkCell(Graphics g, RectangleF r)
    {
        float p = 20 * _s, x = r.X + p, right = r.Right - p, ey = r.Y + 30 * _s;
        float labelEnd = Caps(g, "Network", x, ey, Muted);
        TextRightClipped(g, _t.NetName, _fBody, Muted, labelEnd + 16 * _s, right, ey);

        Rate(g, x, r.Y + 70 * _s, _t.NetDown, true, Accent);
        Rate(g, x, r.Y + 96 * _s, _t.NetUp, false, Second);

        // Download grows up from the centre line, upload grows down.
        float col = 132 * _s;
        var plot = RectangleF.FromLTRB(x + col, r.Y + 44 * _s, right, r.Bottom - 14 * _s);
        if (plot.Width > 40 * _s)
        {
            float mid = plot.Y + plot.Height / 2;
            Hairline(g, plot.X, plot.Right, mid, 20);
            float max = MathF.Max(24 * 1024, MathF.Max(_t.DownHistory.Max(), _t.UpHistory.Max()));
            float amp = plot.Height / 2 - 2 * _s;
            Area(g, plot, _t.DownHistory, max, Accent, mid, amp, false);
            Area(g, plot, _t.UpHistory, max, Second, mid + 1, -amp, false, 56);
        }
    }

    void Rate(Graphics g, float x, float baseline, float bytesPerSecond, bool down, Color arrow)
    {
        Arrow(g, x + 4 * _s, baseline - 7 * _s, down, arrow);
        var (value, unit) = Format.Rate(bytesPerSecond);
        ValueUnit(g, value, unit, _fVal, _fUnit, x + 17 * _s, baseline, Ink);
    }

    void StorageCell(Graphics g, RectangleF r)
    {
        float p = 20 * _s, x = r.X + p, right = r.Right - p, ey = r.Y + 30 * _s;
        float labelEnd = Caps(g, "Storage", x, ey, Muted);
        var drives = _t.Drives;
        if (drives.Count == 0) return;

        if (drives.Count == 1)
        {
            var d = drives[0];
            float frac = d.Total > 0 ? (float)d.Used / d.Total : 0;
            Text(g, d.Name, _fNum, Faint, labelEnd + 8 * _s, ey);
            ValueUnitRight(g, Number(d.Used), $"/ {Format.Bytes(d.Total, binary: true)}", _fNum, _fUnit, right, ey, Soft);
            float after = Text(g, MathF.Round(frac * 100).ToString("0"), _fMid, Ink, x - 2 * _s, r.Y + 74 * _s);
            Text(g, "%", _fUnit, Muted, after + 3 * _s, r.Y + 74 * _s);
            Bar(g, x, r.Bottom - 24 * _s, right - x, frac, Fullness(frac));
            return;
        }

        Text(g, drives.Count.ToString(), _fUnit, Faint, labelEnd + 9 * _s, ey);
        float rowH = 24 * _s, y = r.Y + 46 * _s;
        int fit = Math.Max(1, (int)((r.Bottom - 10 * _s - y) / rowH));
        int shown = Math.Min(drives.Count, fit);
        float textW = Width(g, "000 GB / 0.00 TB", _fUnit);
        for (int i = 0; i < shown; i++)
        {
            var d = drives[i];
            float frac = d.Total > 0 ? (float)d.Used / d.Total : 0, baseline = y + 15 * _s;
            bool more = i == shown - 1 && drives.Count > shown;
            Text(g, d.Name, _fNum, Ink, x, baseline);
            TextRight(g, more ? $"+{drives.Count - shown} more" : $"{Format.Bytes(d.Used, binary: true)} / {Format.Bytes(d.Total, binary: true)}", _fUnit, more ? Faint : Muted, right, baseline);
            float bx = x + 30 * _s;
            Bar(g, bx, baseline - 5 * _s, right - textW - 12 * _s - bx, frac, Fullness(frac), 3);
            y += rowH;
        }
    }

    static string Number(long bytes)
    {
        double gb = bytes / 1073741824.0;
        return gb >= 1000 ? (gb / 1024).ToString("0.00") : gb >= 100 ? gb.ToString("0") : gb.ToString("0.0");
    }

    void ProcessPanel(Graphics g, RectangleF r)
    {
        Plate(g, r);
        float p = 20 * _s, x = r.X + p, right = r.Right - p, ey = r.Y + 30 * _s;
        bool net = _t.NetPerProcess;
        int cols = net ? 5 : 3, sort = _t.Processes.SortKey;
        float barCol = 136 * _s, netCol = 96 * _s;
        Span<float> rights = stackalloc float[5];
        rights[cols - 1] = right;
        for (int c = cols - 2; c >= 0; c--) rights[c] = rights[c + 1] - (c + 1 >= 3 ? netCol : barCol);
        float nameRight = rights[0] - barCol;

        float end = ColumnHeader(g, "Processes", 0, sort, x, ey, false);
        end = Text(g, _t.Processes.Rows.Count.ToString(), _fUnit, Faint, end + (sort == 0 ? 18 : 9) * _s, ey);
        if (!_t.Elevated)
        {
            // Per-app network needs administrator rights; offer it where those columns would be.
            const string hint = "Restart as admin for network per app";
            float hx = end + 20 * _s, hw = Width(g, hint, _fLabel);
            if (hx + hw + 12 * _s < nameRight)
            {
                var link = new RectangleF(hx - 6 * _s, ey - 15 * _s, hw + 12 * _s, 22 * _s);
                Hits.Add((link, HitElevate));
                Text(g, hint, _fLabel, Hover(link) ? Ink : Faint, hx, ey);
            }
        }
        ReadOnlySpan<string> titles = ["CPU", "GPU", "Memory", "Down", "Up"];
        for (int c = 0; c < cols; c++) ColumnHeader(g, titles[c], c + 1, sort, rights[c], ey, true);

        float y = ey + 13 * _s, rowH = 28 * _s;
        Hairline(g, x, right, y);
        y += 3 * _s;
        var rows = _t.Processes.Rows;
        int fit = (int)((r.Bottom - 8 * _s - y) / rowH);
        float memTotal = MathF.Max(1, _t.MemTotal);
        for (int i = 0; i < fit && i < rows.Count; i++)
        {
            var row = rows[i];
            var band = new RectangleF(x - 10 * _s, y, right - x + 20 * _s, rowH);
            Hits.Add((band, HitRow + i));
            if (Hover(band)) FillRound(g, band, 7 * _s, White(8));
            float baseline = y + 18.5f * _s;

            float nw = MathF.Min(Width(g, row.Name, _fBody), nameRight - x - 44 * _s);
            TextClipped(g, row.Name, _fBody, Ink, x, baseline, nameRight - x - 40 * _s);
            if (row.Count > 1)
            {
                string count = row.Count.ToString();
                float cw = Width(g, count, _fTiny) + 10 * _s;
                var chip = new RectangleF(x + nw + 8 * _s, baseline - 12 * _s, cw, 16 * _s);
                FillRound(g, chip, 4.5f * _s, White(9));
                Text(g, count, _fTiny, Muted, chip.X + 5 * _s, baseline - 0.5f * _s);
            }

            float barY = baseline - 5 * _s;
            BarCell(g, Percent(row.Cpu), row.Cpu / 100, row.Cpu >= 0.05f, sort == 1, rights[0], baseline, barY);
            BarCell(g, Percent(row.Gpu), row.Gpu / 100, row.Gpu >= 0.05f, sort == 2, rights[1], baseline, barY);
            string mem = row.Mem >= 10L << 30 ? $"{row.Mem / 1073741824.0:0.0} GB" : $"{row.Mem / 1048576.0:N0} MB";
            BarCell(g, mem, row.Mem / memTotal, row.Mem >= 1 << 20, sort == 3, rights[2], baseline, barY);
            if (net)
            {
                var d = Format.Rate(row.Down); var u = Format.Rate(row.Up);
                TextRight(g, $"{d.Value} {d.Unit}", _fNum, row.Down >= 1 ? Ink : Faint, rights[3], baseline);
                TextRight(g, $"{u.Value} {u.Unit}", _fNum, row.Up >= 1 ? Ink : Faint, rights[4], baseline);
            }
            y += rowH;
        }
    }

    static string Percent(float v) => v < 0.05f ? "0%" : v < 10 ? $"{v:0.0}%" : $"{v:0}%";

    /// <summary>A figure with a small proportional bar to its left.</summary>
    void BarCell(Graphics g, string text, float fraction, bool active, bool sorted, float right, float baseline, float barY)
    {
        TextRight(g, text, _fNum, active ? Ink : Faint, right, baseline);
        float bw = 40 * _s, bx = right - 70 * _s - bw;
        // Square-root scale so small shares stay visible.
        Bar(g, bx, barY, bw, active ? MathF.Sqrt(Math.Clamp(fraction, 0, 1)) : 0, sorted ? Accent : Rgb(0xAEB4BD), 3);
    }

    /// <summary>A sortable column title: the panel's eyebrow for the name column, sentence case for the figures.</summary>
    float ColumnHeader(Graphics g, string title, int key, int sort, float anchor, float baseline, bool rightAligned)
    {
        bool caps = key == 0;
        float width = caps ? CapsWidth(g, title) : Width(g, title, _fLabel);
        float x = rightAligned ? anchor - width : anchor;
        float tri = key == sort ? 13 * _s : 0;
        var hit = rightAligned
            ? new RectangleF(x - tri - 8 * _s, baseline - 16 * _s, width + tri + 16 * _s, 24 * _s)
            : new RectangleF(x - 8 * _s, baseline - 16 * _s, width + tri + 16 * _s, 24 * _s);
        Hits.Add((hit, HitSort + key));
        Color c = key == sort || Hover(hit) ? Ink : Muted;
        float end = caps ? Caps(g, title, x, baseline, c) : Text(g, title, _fLabel, c, x, baseline);
        if (key == sort)
        {
            float ts = 3.4f * _s, tx = rightAligned ? x - 12 * _s : end + 5 * _s, ty = baseline - 6.5f * _s;
            g.FillPolygon(Brush(Accent), [new PointF(tx, ty), new PointF(tx + 2 * ts, ty), new PointF(tx + ts, ty + ts * 1.25f)]);
        }
        return end;
    }

    // ---------------------------------------------------------------- grouped lists (Specs, Settings, Lighting)

    /// <summary>An eyebrow over a group. Returns the top of the group's plate.</summary>
    float GroupHeading(Graphics g, string title, float x, float y, string? icon = null)
    {
        float baseline = y + 12 * _s;
        if (icon != null)
        {
            Icon(g, icon, x, baseline - 12 * _s, Muted, 14);
            x += 22 * _s;
        }
        Caps(g, title, x, baseline, Muted);
        return y + 24 * _s;
    }

    /// <summary>A label with an optional quieter line under it, for a settings row starting at <paramref name="top"/>.</summary>
    void RowText(Graphics g, string label, string? note, float x, float top, float maxWidth, float height)
    {
        if (note == null) { TextClipped(g, label, _fBody, Ink, x, top + height / 2 + 5 * _s, maxWidth); return; }
        TextClipped(g, label, _fBody, Ink, x, top + height / 2 - 3 * _s, maxWidth);
        TextClipped(g, note, _fLabel, Muted, x, top + height / 2 + 15 * _s, maxWidth);
    }

    /// <summary>A whole-row switch: the row is the target, and it lights up under the pointer.</summary>
    void SwitchRow(Graphics g, RectangleF row, string label, string note, bool on, int id)
    {
        Hits.Add((row, id));
        if (Hover(row)) FillRound(g, RectangleF.Inflate(row, -5 * _s, -4 * _s), 8 * _s, White(5));
        float p = 18 * _s;
        RowText(g, label, note, row.X + p, row.Y, row.Width - 2 * p - 56 * _s, row.Height);
        SwitchAt(g, on, row.Right - p, row.Y + row.Height / 2);
    }

    // ---------------------------------------------------------------- specs

    static string? Row(List<SpecSection> specs, string section, string key)
    {
        foreach (var s in specs)
            if (s.Title == section)
                foreach (var (k, v) in s.Rows)
                    if (k == key) return v;
        return null;
    }

    void RenderSpecs(Graphics g, RectangleF a)
    {
        if (Specs == null)
        {
            Text(g, "Reading hardware…", _fBody, Muted, a.X, a.Y + 30 * _s);
            return;
        }
        float gap = 12 * _s, rowH = 34 * _s, p = 18 * _s;
        var state = g.Save();
        g.SetClip(RectangleF.FromLTRB(RailWidth * _s + 1, a.Y - 2 * _s, a.Right + 20 * _s, a.Bottom + 18 * _s));
        float top = a.Y - Scroll;

        // Identity, straight on the page
        string device = Row(Specs, "System", "Device") ?? Row(Specs, "System", "Motherboard") ?? "This PC";
        string os = Row(Specs, "System", "Operating system") ?? "Windows";
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : up.TotalHours >= 1 ? $"{up.Hours}h {up.Minutes}m" : $"{up.Minutes}m";
        string build = Row(Specs, "System", "Build") ?? "";
        float sx = a.Right, sb = top + 52 * _s;
        sx = Fact(g, "Uptime", uptime, sx, sb) - 32 * _s;
        if (build.Length > 0) sx = Fact(g, "Build", build, sx, sb) - 32 * _s;
        TextClipped(g, device, _fTitle, Ink, a.X - 1 * _s, top + 34 * _s, sx - a.X);
        TextClipped(g, os, _fBody, Muted, a.X, top + 58 * _s, sx - a.X);
        float y = top + 80 * _s;

        // Key figures: one plate, divided
        if (Summary is { } sum)
        {
            _figures.Clear();
            if (sum.Cores > 0 || sum.Threads > 0)
                _figures.Add(("Processor", sum.Cores > 0 ? sum.Cores.ToString() : sum.Threads.ToString(), sum.Cores > 0 ? "cores" : "threads",
                    sum.BaseMhz > 0 ? $"{sum.Threads} threads  ·  {sum.BaseMhz / 1000.0:0.0} GHz" : $"{sum.Threads} threads"));
            if (sum.VramBytes > 0)
                _figures.Add(("Graphics", (sum.VramBytes / 1073741824.0).ToString("0.#"), "GB", sum.GpuName.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("Intel(R) ", "")));
            if (sum.RamBytes > 0)
                _figures.Add(("Memory", (sum.RamBytes / 1073741824.0).ToString("0.#"), "GB", sum.RamSpeed > 0 ? $"{sum.RamType}  ·  {sum.RamSpeed} MHz".Trim(' ', '·') : sum.RamType));
            if (sum.StorageBytes > 0)
            {
                double gb = sum.StorageBytes / 1e9;
                _figures.Add(("Storage", gb >= 1000 ? (gb / 1000).ToString("0.##") : gb.ToString("0"), gb >= 1000 ? "TB" : "GB", sum.Drives == 1 ? "1 drive" : $"{sum.Drives} drives"));
            }
            if (sum.ScreenHz > 0)
                _figures.Add(("Display", sum.ScreenHz.ToString(), "Hz", $"{sum.ScreenW} × {sum.ScreenH}"));

            int n = _figures.Count;
            if (n > 0)
            {
                var plate = new RectangleF(a.X, y, a.Width, 108 * _s);
                Plate(g, plate);
                float cw = plate.Width / n;
                for (int i = 0; i < n; i++)
                {
                    var (label, value, unit, note) = _figures[i];
                    float x = plate.X + i * cw + 20 * _s, right = plate.X + (i + 1) * cw - 14 * _s;
                    if (i > 0) Divider(g, plate.X + i * cw, plate.Y + 18 * _s, plate.Bottom - 18 * _s);
                    Icon(g, label, x, plate.Y + 18 * _s, Muted, 14);
                    Text(g, label, _fLabel, Muted, x + 22 * _s, plate.Y + 30 * _s);
                    ValueUnit(g, value, unit, _fMid, _fUnit, x - 2 * _s, plate.Y + 72 * _s, Ink);
                    TextClipped(g, note, _fLabel, Muted, x, plate.Y + 93 * _s, right - x);
                }
                y = plate.Bottom + 26 * _s;
            }
        }

        // Detail groups in two columns
        int cols = a.Width > 700 * _s ? 2 : 1;
        float colW = (a.Width - (cols - 1) * gap * 1.5f) / cols;
        Span<float> cy = stackalloc float[2];
        cy[0] = cy[1] = y;
        foreach (var sec in Specs)
        {
            int c = cols == 2 && cy[1] < cy[0] ? 1 : 0;
            float gx = a.X + c * (colW + gap * 1.5f);
            float height = 24 * _s + sec.Rows.Count * rowH;
            if (cy[c] + height > a.Y - 40 * _s && cy[c] < a.Bottom + 40 * _s)
            {
                float plateTop = GroupHeading(g, sec.Title, gx, cy[c], sec.Title);
                var plate = new RectangleF(gx, plateTop, colW, sec.Rows.Count * rowH);
                Plate(g, plate);
                float x = plate.X + p, right = plate.Right - p, keyW = Math.Clamp(colW * 0.32f, 110 * _s, 150 * _s);
                for (int i = 0; i < sec.Rows.Count; i++)
                {
                    var (key, value) = sec.Rows[i];
                    float ry = plate.Y + i * rowH, baseline = ry + rowH / 2 + 4.5f * _s;
                    bool child = key.StartsWith("  ");
                    float indent = child ? 14 * _s : 0, valueRight = right;
                    if (i > 0) Hairline(g, x + indent, right, ry, 10);
                    if (sec.Meters.TryGetValue(key, out float fill))
                    {
                        TextRight(g, $"{fill * 100:0}%", _fNum, Soft, right, baseline);
                        float mw = 64 * _s, mx = right - Width(g, "100%", _fNum) - 12 * _s - mw;
                        Bar(g, mx, baseline - 6 * _s, mw, fill, Fullness(fill));
                        valueRight = mx - 14 * _s;
                    }
                    TextClipped(g, key.Trim(), _fBody, child ? Faint : Muted, x + indent, baseline, keyW - indent - 8 * _s);
                    TextClipped(g, value, _fBody, Ink, x + keyW, baseline, valueRight - x - keyW);
                }
            }
            cy[c] += height + 22 * _s;
        }
        g.Restore(state);

        float content = MathF.Max(cy[0], cy[1]) + Scroll - 22 * _s - a.Y;
        MaxScroll = MathF.Max(0, content - a.Height);
        if (Scroll > MaxScroll) Scroll = MaxScroll;
    }

    /// <summary>A small labelled fact, right-aligned at <paramref name="right"/>. Returns its left edge.</summary>
    float Fact(Graphics g, string label, string value, float right, float baseline)
    {
        float w = MathF.Max(Width(g, label, _fLabel), Width(g, value, _fNum));
        TextRight(g, label, _fLabel, Faint, right, baseline - 20 * _s);
        TextRight(g, value, _fNum, Ink, right, baseline);
        return right - w;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Changes the accent. Cached brushes are dropped so a hue drag does not accumulate them.</summary>
    public void SetAccent(Color c)
    {
        Accent = c;
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
    }

    public void SetHueFromX(float x)
        => SetAccent(FromHsv(Math.Clamp((x - HueRect.X) / MathF.Max(1, HueRect.Width), 0, 1) * 359.9f, HueSaturation, 1));

    static Color FromHsv(float hue, float s, float v)
    {
        float c = v * s, h = hue / 60, x = c * (1 - MathF.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        return Color.FromArgb(255, (int)MathF.Round((r + m) * 255), (int)MathF.Round((g + m) * 255), (int)MathF.Round((b + m) * 255));
    }

    void RenderSettings(Graphics g, RectangleF a)
    {
        float width = MathF.Min(a.Width, 660 * _s), x = a.X + MathF.Max(0, (a.Width - width) / 2);
        float rowH = 60 * _s, p = 18 * _s;
        var state = g.Save();
        g.SetClip(RectangleF.FromLTRB(RailWidth * _s + 1, a.Y - 2 * _s, a.Right + 20 * _s, a.Bottom + 18 * _s));
        float y = a.Y + 6 * _s - Scroll;

        // Appearance
        float plateTop = GroupHeading(g, "Appearance", x, y);
        if (Accent.ToArgb() != DefaultAccent.ToArgb())
        {
            const string reset = "Reset to default";
            float rw = Width(g, reset, _fLabel);
            var link = new RectangleF(x + width - rw - 6 * _s, y - 4 * _s, rw + 12 * _s, 22 * _s);
            Hits.Add((link, HitReset));
            TextRight(g, reset, _fLabel, Hover(link) ? Ink : Muted, x + width, y + 12 * _s);
        }
        var plate = new RectangleF(x, plateTop, width, rowH * 2);
        Plate(g, plate);
        float px = plate.X + p, pr = plate.Right - p;
        float size = 20 * _s, pitch = 30 * _s, swatchW = pitch * (Presets.Length - 1) + size;
        RowText(g, "Accent colour", "Charts, highlights and switches", px, plate.Y, pr - swatchW - px - 16 * _s, rowH);
        float sy = plate.Y + rowH / 2 - size / 2;
        for (int i = 0; i < Presets.Length; i++)
        {
            var dot = new RectangleF(pr - swatchW + i * pitch, sy, size, size);
            var hit = RectangleF.Inflate(dot, 5 * _s, 6 * _s);
            Hits.Add((hit, HitSwatch + i));
            g.FillEllipse(Brush(Presets[i]), dot);
            bool selected = Presets[i].ToArgb() == Accent.ToArgb();
            if (selected || Hover(hit))
            {
                using var ring = new Pen(selected ? Ink : White(90), 1.5f * _s);
                g.DrawEllipse(ring, RectangleF.Inflate(dot, 3.5f * _s, 3.5f * _s));
            }
        }
        Hairline(g, px, pr, plate.Y + rowH, 10);
        RowText(g, "Custom colour", $"#{Accent.R:X2}{Accent.G:X2}{Accent.B:X2}", px, plate.Y + rowH, pr - swatchW - px - 16 * _s, rowH);
        float hh = 10 * _s;
        HueRect = new RectangleF(pr - swatchW, plate.Y + rowH * 1.5f - hh / 2, swatchW, hh);
        Hits.Add((RectangleF.Inflate(HueRect, 9 * _s, 12 * _s), HitHue));
        HueStrip(g, HueRect);
        y = plate.Bottom + 26 * _s;

        // Startup
        plateTop = GroupHeading(g, "Startup", x, y);
        plate = new RectangleF(x, plateTop, width, rowH * 2);
        Plate(g, plate);
        SwitchRow(g, new RectangleF(x, plate.Y, width, rowH), "Start with Windows", "Opens in the tray when you sign in, without the window", StartWithWindows, HitAutoStart);
        Hairline(g, px, pr, plate.Y + rowH, 10);
        SwitchRow(g, new RectangleF(x, plate.Y + rowH, width, rowH), "Keep running in the tray", "The close button hides Gauge instead of quitting", KeepInTray, HitTray);
        y = plate.Bottom + 26 * _s;

        // Updates
        plateTop = GroupHeading(g, "Updates", x, y);
        plate = new RectangleF(x, plateTop, width, rowH * 2);
        Plate(g, plate);
        var updates = Updates;
        string state2 = "Updates are off";
        bool ready = false;
        if (updates != null)
        {
            lock (updates.Sync)
            {
                ready = updates.Status == UpdateStatus.Ready;
                state2 = updates.Status switch
                {
                    UpdateStatus.NotInstalled => "Only the installed version updates itself",
                    UpdateStatus.NoServer => "No update server set",
                    UpdateStatus.UpToDate => "Up to date",
                    UpdateStatus.Checking => "Checking for updates",
                    UpdateStatus.Downloading => $"Downloading {updates.NewVersion}  ·  {updates.Progress}%",
                    UpdateStatus.Ready => $"Version {updates.NewVersion} is ready to install",
                    UpdateStatus.Failed => "Could not check for updates; trying again shortly",
                    _ => "Updates are off",
                };
            }
        }
        float buttonLeft = pr;
        if (ready) buttonLeft = Button(g, "Restart to update", HitUpdate, pr, plate.Y + rowH / 2, Accent) - 12 * _s;
        RowText(g, $"Gauge {Program.Version} beta", state2, px, plate.Y, buttonLeft - px, rowH);
        Hairline(g, px, pr, plate.Y + rowH, 10);
        SwitchRow(g, new RectangleF(x, plate.Y + rowH, width, rowH), "Download updates automatically", "New versions install the next time Gauge starts", updates?.Automatic == true, HitAutoUpdate);
        y = plate.Bottom;

        g.Restore(state);
        MaxScroll = MathF.Max(0, y + Scroll - a.Y - a.Height);
        if (Scroll > MaxScroll) Scroll = MaxScroll;
    }

    void HueStrip(Graphics g, RectangleF r)
    {
        using (var path = Round(r, r.Height / 2))
        using (var hue = new LinearGradientBrush(new PointF(r.X - 1, 0), new PointF(r.Right + 1, 0), Color.Black, Color.Black))
        {
            var blend = new ColorBlend(13);
            for (int i = 0; i < 13; i++)
            {
                blend.Positions[i] = i / 12f;
                blend.Colors[i] = FromHsv(MathF.Min(359.9f, i * 30), HueSaturation, 1);
            }
            hue.InterpolationColors = blend;
            g.FillPath(hue, path);
        }
        float tx = r.X + Accent.GetHue() / 360f * r.Width, ty = r.Y + r.Height / 2, outer = 8.5f * _s, inner = 5.5f * _s;
        g.FillEllipse(Brush(Color.FromArgb(70, 0, 0, 0)), tx - outer - 1 * _s, ty - outer, (outer + 1 * _s) * 2, (outer + 1 * _s) * 2);
        g.FillEllipse(Brush(Ink), tx - outer, ty - outer, outer * 2, outer * 2);
        g.FillEllipse(Brush(Accent), tx - inner, ty - inner, inner * 2, inner * 2);
    }

    // ---------------------------------------------------------------- icons

    /// <summary>Line icons on a 16-unit grid, drawn at <paramref name="size"/> units.</summary>
    void Icon(Graphics g, string kind, float x, float y, Color c, float size = 16)
    {
        float k = _s * size / 16;
        using var pen = new Pen(c, 1.35f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, x + x1 * k, y + y1 * k, x + x2 * k, y + y2 * k);
        void Box(float bx, float by, float bw, float bh, float radius = 1.5f)
        {
            using var path = Round(new RectangleF(x + bx * k, y + by * k, bw * k, bh * k), radius * k);
            g.DrawPath(pen, path);
        }
        void Circle(float cx, float cy, float r) => g.DrawEllipse(pen, x + (cx - r) * k, y + (cy - r) * k, r * 2 * k, r * 2 * k);
        switch (kind)
        {
            case "Monitor": // a pulse
                g.DrawLines(pen, [new PointF(x + 0.5f * k, y + 8.5f * k), new PointF(x + 4 * k, y + 8.5f * k), new PointF(x + 6 * k, y + 3 * k),
                    new PointF(x + 9.5f * k, y + 13.5f * k), new PointF(x + 11.5f * k, y + 6.5f * k), new PointF(x + 13 * k, y + 8.5f * k), new PointF(x + 15.5f * k, y + 8.5f * k)]);
                break;
            case "Lighting": // three overlapping lights: red, green, blue
                Circle(8, 5.6f, 4.1f);
                Circle(5.3f, 10.3f, 4.1f);
                Circle(10.7f, 10.3f, 4.1f);
                break;
            case "Settings": // two sliders
                foreach (var (ly, kx) in (ReadOnlySpan<(float, float)>)[(4.5f, 10.5f), (11.5f, 5.5f)])
                {
                    Line(1, ly, kx - 2.6f, ly);
                    Line(kx + 2.6f, ly, 15, ly);
                    Circle(kx, ly, 2);
                }
                break;
            case "Processor" or "Specs":
                Box(3, 3, 10, 10);
                Box(6, 6, 4, 4, 0.8f);
                foreach (float t in (ReadOnlySpan<float>)[5.5f, 10.5f])
                {
                    Line(t, 0.5f, t, 3); Line(t, 13, t, 15.5f);
                    Line(0.5f, t, 3, t); Line(13, t, 15.5f, t);
                }
                break;
            case "Graphics":
                Box(0.5f, 3, 15, 9);
                Circle(10.5f, 7.5f, 2.5f);
                Line(3, 6, 5.5f, 6); Line(3, 9, 5.5f, 9);
                Line(3, 12, 3, 14.5f); Line(7, 12, 7, 14.5f);
                break;
            case "Memory":
                Box(0.5f, 4, 15, 7);
                Line(4, 6.5f, 4, 8.5f); Line(8, 6.5f, 8, 8.5f); Line(12, 6.5f, 12, 8.5f);
                Line(3, 11, 3, 13.5f); Line(6.3f, 11, 6.3f, 13.5f); Line(9.7f, 11, 9.7f, 13.5f); Line(13, 11, 13, 13.5f);
                break;
            case "Storage":
                Box(1.5f, 2, 13, 12, 2.5f);
                Line(1.5f, 9.5f, 14.5f, 9.5f);
                Line(11, 11.8f, 11.6f, 11.8f);
                break;
            case "Display":
                Box(0.5f, 2, 15, 9.5f);
                Line(8, 11.5f, 8, 14.5f); Line(5, 14.5f, 11, 14.5f);
                break;
            case "Keyboard":
                Box(0.5f, 3.5f, 15, 9);
                Line(3.5f, 6.5f, 3.6f, 6.5f); Line(6.5f, 6.5f, 6.6f, 6.5f); Line(9.5f, 6.5f, 9.6f, 6.5f); Line(12.5f, 6.5f, 12.6f, 6.5f);
                Line(4.5f, 9.5f, 11.5f, 9.5f);
                break;
            case "Mouse":
                Box(4, 1, 8, 14, 4);
                Line(8, 3.5f, 8, 6);
                break;
            case "Headset":
                g.DrawArc(pen, x + 2 * k, y + 1.5f * k, 12 * k, 12 * k, 180, 180);
                Box(1, 8, 3.5f, 6, 1.2f);
                Box(11.5f, 8, 3.5f, 6, 1.2f);
                break;
            case "Network":
                foreach (float radius in (ReadOnlySpan<float>)[3.5f, 7, 10.5f])
                    g.DrawArc(pen, x + (8 - radius) * k, y + (13.5f - radius) * k, radius * 2 * k, radius * 2 * k, 225, 90);
                Line(8, 13.3f, 8, 13.7f);
                break;
            default: // System
                Box(2.5f, 3, 11, 8);
                Line(0.5f, 13.5f, 15.5f, 13.5f);
                break;
        }
    }

    void DisposeFonts()
    {
        foreach (var f in (ReadOnlySpan<Font?>)[_fCaps, _fTiny, _fLabel, _fBody, _fStrong, _fNum, _fVal, _fUnit, _fHero, _fHeroUnit, _fMid, _fTitle, _fPage, _fRail, _fSection]) f?.Dispose();
    }

    public void Dispose()
    {
        DisposeFonts();
        foreach (var b in _brushes.Values) b.Dispose();
        _sf.Dispose();
        _sfEllipsis.Dispose();
        _sfWrap.Dispose();
    }
}
