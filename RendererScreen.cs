using System.Drawing.Drawing2D;

namespace Gauge;

/// <summary>A picture being cropped for the Kraken's screen: the source's first frame and the square chosen on it, in source pixels.</summary>
internal sealed class CropState
{
    public required string Path;
    public required Bitmap Image;
    public int Frames = 1;
    public RectangleF Box;
    /// <summary>Where the picture was drawn last time and at what scale, for turning pointer positions into source pixels.</summary>
    public RectangleF View;
    public float Scale = 1;
    public PointF Grab;
}

// The Kraken's screen on the Lighting page: a list entry, an editor with a live preview, and the crop dialog for pictures.
internal sealed partial class Renderer
{
    // Hit ids: the first group in the free range above the settings page's, the rest above it
    public const int HitLcdItem = 130, HitLcdGauge = 131, HitLcdOwn = 132, HitLcdReading = 133, // 133..139
        HitLcdStyle = 140, HitLcdPreset = 142, HitLcdHue1 = 148, HitLcdHue2 = 149,              // 140..141, 142..146
        HitLcdBright = 150, HitLcdLogo = 158,                                                   // 150..153
        HitLcdMode = 300, HitLcdDial = 303,                                                     // 300..302, 303..314
        HitLcdReading1 = 320, HitLcdReading2 = 330,                                             // 320..326, 330..336
        HitLcdNumber = 340, HitLcdLabel = 350,                                                  // five swatches per reading: 340..349, 350..359
        HitLcdUpload = 360, HitLcdNoMedia = 361, HitLcdMedia = 370, HitLcdMediaDelete = 400,     // 370..399, 400..429
        HitCropShade = 440, HitCropBox = 441, HitCropUse = 442, HitCropCancel = 443,
        HitLcdLast = 449;
    const int MaxMediaTiles = 30;

    static readonly (string Name, Color A, Color B)[] s_lcdPresets =
    [
        ("Violet", Rgb(0x6B00DE), Rgb(0xD600BF)), ("Amber", Rgb(0xFFA53C), Rgb(0xFF4D4D)), ("Ocean", Rgb(0x0057FF), Rgb(0x00D0FF)),
        ("Lime", Rgb(0x00B37E), Rgb(0xB8E04A)), ("Mono", Rgb(0x8E96A3), Rgb(0xFFFFFF)),
    ];
    static readonly string[] s_readings = ["Liquid", "CPU temp", "CPU load", "CPU clock", "GPU temp", "GPU load", "GPU clock"];
    static readonly int[] s_lcdBrightness = [25, 50, 75, 100];

    bool _screenSelected, _screenShown;
    RectangleF _lcdHue1, _lcdHue2;
    readonly List<string> _mediaTiles = new(); // library ids in the order their tiles were drawn

    /// <summary>The crop dialog, while it is open.</summary>
    public CropState? Crop;

    /// <summary>Text colours for Dual: white, two greys, and the two arc colours.</summary>
    static Color[] TextSwatches(LcdSettings s) => [Color.White, Rgb(0xC4C9D2), Rgb(0x7D848F), s.Arc1, s.Arc2];

    /// <summary>Handles a click or drag on the screen's controls. Settings are saved and the frame redrawn by the screen itself.</summary>
    public void ScreenPress(int id, float x)
    {
        var screen = Lights?.Screen;
        if (id == HitLcdItem) { _screenSelected = true; return; }
        if (screen == null) return;
        bool dual = screen.Settings.Mode == LcdMode.Dual;
        switch (id)
        {
            case HitLcdGauge or HitLcdOwn: screen.Change(s => s.Drive = id == HitLcdGauge); break;
            case >= HitLcdMode and < HitLcdMode + 3: screen.Change(s => s.Mode = (LcdMode)(id - HitLcdMode)); break;
            case >= HitLcdReading and < HitLcdReading + 7: screen.Change(s => s.Reading = (LcdReading)(id - HitLcdReading)); break;
            case >= HitLcdReading1 and < HitLcdReading1 + 7: screen.Change(s => s.Reading1 = (LcdReading)(id - HitLcdReading1)); break;
            case >= HitLcdReading2 and < HitLcdReading2 + 7: screen.Change(s => s.Reading2 = (LcdReading)(id - HitLcdReading2)); break;
            case HitLcdStyle or HitLcdStyle + 1: screen.Change(s => s.Fill = id == HitLcdStyle + 1); break;
            case >= HitLcdPreset and < HitLcdPreset + 5:
                var p = s_lcdPresets[id - HitLcdPreset];
                screen.Change(s => { if (dual) { s.Arc1 = p.A; s.Arc2 = p.B; } else { s.Color1 = p.A; s.Color2 = p.B; } });
                break;
            case HitLcdHue1 or HitLcdHue2:
                var strip = id == HitLcdHue1 ? _lcdHue1 : _lcdHue2;
                float hue = Math.Clamp((x - strip.X) / MathF.Max(1, strip.Width), 0, 1) * 359.9f;
                var c = FromHsv(hue, 1, 0.9f);
                screen.Change(s =>
                {
                    if (dual) { if (id == HitLcdHue1) s.Arc1 = c; else s.Arc2 = c; }
                    else if (id == HitLcdHue1) s.Color1 = c;
                    else s.Color2 = c;
                });
                break;
            case >= HitLcdNumber and < HitLcdNumber + 10:
            {
                int i = id - HitLcdNumber;
                screen.Change(s => { var c = TextSwatches(s)[i % 5]; if (i < 5) s.Number1 = c; else s.Number2 = c; });
                break;
            }
            case >= HitLcdLabel and < HitLcdLabel + 10:
            {
                int i = id - HitLcdLabel;
                screen.Change(s => { var c = TextSwatches(s)[i % 5]; if (i < 5) s.Label1 = c; else s.Label2 = c; });
                break;
            }
            case >= HitLcdBright and < HitLcdBright + 4: screen.Change(s => s.Brightness = s_lcdBrightness[id - HitLcdBright]); break;
            case >= HitLcdDial and < HitLcdDial + 12: screen.Change(s => s.Dial = (id - HitLcdDial) * 30); break;
            case HitLcdLogo: screen.Change(s => s.Logo = !s.Logo); break;
            case HitLcdNoMedia: screen.Change(s => s.Media = ""); break;
            case >= HitLcdMedia and < HitLcdMedia + MaxMediaTiles when id - HitLcdMedia < _mediaTiles.Count:
                string chosen = _mediaTiles[id - HitLcdMedia];
                screen.Change(s => s.Media = chosen);
                break;
            case >= HitLcdMediaDelete and < HitLcdMediaDelete + MaxMediaTiles when id - HitLcdMediaDelete < _mediaTiles.Count:
                screen.DeleteMedia(_mediaTiles[id - HitLcdMediaDelete]);
                break;
        }
    }

    /// <summary>The screen's entry in the device list.</summary>
    void ScreenItem(Graphics g, RectangleF row, KrakenLcd screen, bool hairline, bool on)
    {
        Hits.Add((row, HitLcdItem));
        bool hot = Hover(row);
        if (hairline) Hairline(g, row.X + 14 * _s, row.Right - 14 * _s, row.Y, 10);
        if (on || hot) FillRound(g, RectangleF.Inflate(row, -5 * _s, -4 * _s), 8 * _s, White(on ? 10 : 5));
        if (on) Pill(g, new RectangleF(row.X + 9 * _s, row.Y + row.Height / 2 - 10 * _s, 3 * _s, 20 * _s), Accent);

        var s = screen.Settings;
        float cy = row.Y + row.Height / 2, x = row.X + 22 * _s;
        Icon(g, "Display", x, cy - 8 * _s, on ? Ink : Muted, 16);
        float sw = 30 * _s, tx = x + 28 * _s, tw = row.Right - 16 * _s - sw - 12 * _s - tx;
        TextClipped(g, screen.Name, _fStrong, on || hot ? Ink : Soft, tx, cy - 2 * _s, tw);
        string design = s.Mode switch { LcdMode.Dual => "Dual", LcdMode.Media => "Picture", _ => "Gauge display" };
        string sub = screen.Problem ? (screen.Status.StartsWith("NZXT CAM") ? "Close NZXT CAM to drive it" : "Needs attention") : s.Drive ? $"Screen  ·  {design}" : "Screen  ·  Kraken's own";
        TextClipped(g, sub, _fLabel, screen.Problem && s.Drive ? Warn : Muted, tx, cy + 15 * _s, tw);
        var (a, b) = s.Mode == LcdMode.Dual ? (s.Arc1, s.Arc2) : (s.Color1, s.Color2);
        GradientPill(g, new RectangleF(row.Right - 16 * _s - sw, cy - 6 * _s, sw, 12 * _s), s.Drive ? a : Rgb(0x1A1C20), s.Drive ? b : Rgb(0x1A1C20));
    }

    void GradientPill(Graphics g, RectangleF r, Color a, Color b)
    {
        using var path = Round(r, r.Height / 2);
        using (var brush = new LinearGradientBrush(new PointF(r.X - 1, 0), new PointF(r.Right + 1, 0), a, b)) g.FillPath(brush, path);
        using var edge = new Pen(White(30), 1);
        g.DrawPath(edge, path);
    }

    /// <summary>The screen's editor: a live preview of what it shows, its status, and its settings. Returns its bottom.</summary>
    float ScreenEditor(Graphics g, RectangleF a, KrakenLcd screen)
    {
        var s = screen.Settings;
        float y = a.Y + 6 * _s;
        float labelEnd = Caps(g, "Liquid cooler", a.X, y + 12 * _s, Muted);
        TextRightClipped(g, $"NZXT  ·  {KrakenUsb.Vendor:X4}:{KrakenUsb.Product:X4}", _fLabel, Faint, labelEnd + 20 * _s, a.Right, y + 12 * _s);
        var (glowA, _) = s.Mode == LcdMode.Dual ? (s.Arc1, s.Arc2) : (s.Color1, s.Color2);

        // Stage: the round screen as it looks, beside what Gauge knows about the cooler
        var stage = new RectangleF(a.X, y + 24 * _s, a.Width, 244 * _s);
        Plate(g, stage);
        float size = 204 * _s, p = 20 * _s;
        var circle = new RectangleF(stage.X + p, stage.Y + (stage.Height - size) / 2, size, size);
        using (var glow = new GraphicsPath())
        {
            var halo = RectangleF.Inflate(circle, 26 * _s, 26 * _s);
            glow.AddEllipse(halo);
            using var brush = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(s.Drive && s.Mode != LcdMode.Media ? 46 : 0, glowA), SurroundColors = [Color.FromArgb(0, glowA)] };
            var clip = g.Save();
            using (var plateClip = Round(Snap(stage), PlateRadius * _s)) g.SetClip(plateClip, CombineMode.Intersect);
            g.FillPath(brush, glow);
            g.Restore(clip);
        }
        using (var round = new GraphicsPath())
        {
            round.AddEllipse(circle);
            var state = g.Save();
            g.SetClip(round, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            bool drawn = false;
            lock (screen.Sync)
            {
                if (screen.Preview is { } frame) { g.DrawImage(frame, circle); drawn = true; }
                else g.FillEllipse(Brush(Rgb(0x050506)), circle);
            }
            if (!drawn && s.Mode == LcdMode.Media)
            {
                // No picture chosen: the cooler shows its own liquid-temperature screen
                float mid = circle.Y + circle.Height / 2;
                TextCentred(g, "Liquid temperature", _fStrong, Muted, circle.X + circle.Width / 2, mid);
                TextCentred(g, "The Kraken's own screen", _fLabel, Faint, circle.X + circle.Width / 2, mid + 18 * _s);
            }
            g.Restore(state);
            using var bezel = new Pen(White(28), 1.5f * _s);
            g.DrawEllipse(bezel, circle);
        }

        float tx = circle.Right + 30 * _s, tw = stage.Right - p - tx;
        TextClipped(g, screen.Name, _fSection, Ink, tx, stage.Y + 46 * _s, tw);
        float sy = Paragraph(g, screen.Status, _fLabel, screen.Problem && s.Drive ? Warn : Muted, tx, stage.Y + 56 * _s, tw, 3);
        float progress = screen.Progress;
        if (!float.IsNaN(progress)) Bar(g, tx, sy + 10 * _s, MathF.Min(tw, 220 * _s), progress, Accent);
        else if (!s.Drive) Paragraph(g, "Choose Gauge below to show this design on the cooler. Gauge then holds the cooler, so NZXT CAM has to stay closed.", _fLabel, Faint, tx, sy + 8 * _s, tw, 4);
        float fy = stage.Bottom - 26 * _s;
        float liquid = screen.Liquid, pump = screen.Pump;
        if (!float.IsNaN(liquid))
        {
            Text(g, "Liquid", _fLabel, Muted, tx, fy - 24 * _s);
            ValueUnit(g, liquid.ToString("0.0"), "°C", _fVal, _fUnit, tx - 1 * _s, fy, Ink);
        }
        if (!float.IsNaN(pump))
        {
            float px = tx + 110 * _s;
            Text(g, "Pump", _fLabel, Muted, px, fy - 24 * _s);
            ValueUnit(g, pump.ToString("N0"), "rpm", _fVal, _fUnit, px - 1 * _s, fy, Ink);
        }

        // Settings, as labelled rows chosen by the design
        var area = new RectangleF(a.X, stage.Bottom + 16 * _s, a.Width, 0);
        float lp = 18 * _s, labelW = 104 * _s, cx = area.X + lp + labelW, right = area.Right - lp, cw = right - cx;
        float rowH = 60 * _s, colourH = 128 * _s, dialH = 124 * _s;
        var rows = new List<(string? Label, float Height, Action<float> Draw)>();

        rows.Add(("Screen", rowH, ry => Segmented(g, cx, ry + 13 * _s, ["Gauge", "Kraken's own"], s.Drive ? 0 : 1, HitLcdGauge, true)));
        rows.Add(("Design", rowH, ry => Segmented(g, cx, ry + 13 * _s, ["Single", "Dual", "Picture"], (int)s.Mode, HitLcdMode, true)));
        switch (s.Mode)
        {
            case LcdMode.Single:
                rows.Add(ReadingRow(g, "Shows", s.Reading, HitLcdReading, cx, cw));
                rows.Add(("Style", rowH, ry => Segmented(g, cx, ry + 13 * _s, ["Ring", "Fill"], s.Fill ? 1 : 0, HitLcdStyle, true)));
                rows.Add(("Colours", colourH, ry => ColourRow(g, ry, cx, cw, s.Color1, s.Color2, "1", "2")));
                break;
            case LcdMode.Dual:
                rows.Add(ReadingRow(g, "Left", s.Reading1, HitLcdReading1, cx, cw));
                rows.Add(ReadingRow(g, "Right", s.Reading2, HitLcdReading2, cx, cw));
                rows.Add(("Arcs", colourH, ry => ColourRow(g, ry, cx, cw, s.Arc1, s.Arc2, "L", "R")));
                rows.Add(("Numbers", rowH, ry => SwatchPair(g, ry + rowH / 2, cx, TextSwatches(s), s.Number1, s.Number2, HitLcdNumber)));
                rows.Add(("Labels", rowH, ry => SwatchPair(g, ry + rowH / 2, cx, TextSwatches(s), s.Label1, s.Label2, HitLcdLabel)));
                break;
            default:
                rows.Add(MediaRow(g, screen, s, cx, cw));
                break;
        }
        rows.Add(("Brightness", rowH, ry =>
        {
            int bi = Array.FindIndex(s_lcdBrightness, b => b >= s.Brightness);
            Segmented(g, cx, ry + 13 * _s, ["25%", "50%", "75%", "100%"], bi < 0 ? 3 : bi, HitLcdBright, true);
        }));
        rows.Add(("Rotation", dialH, ry => DialRow(g, cx, ry, dialH, s.Dial, cw)));
        if (s.Mode != LcdMode.Media) rows.Add((null, rowH, ry => SwitchRow(g, new RectangleF(area.X, ry, area.Width, rowH), "Show the Gauge mark", "Above the numbers, where NZXT CAM puts its logo", s.Logo, HitLcdLogo)));

        var plate = new RectangleF(area.X, area.Y, area.Width, rows.Sum(r => r.Height));
        Plate(g, plate);
        float top = plate.Y;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) Hairline(g, plate.X + lp, right, top, 10);
            if (rows[i].Label is { } label) Text(g, label, _fBody, Muted, plate.X + lp, top + 34.5f * _s);
            rows[i].Draw(top);
            top += rows[i].Height;
        }
        return plate.Bottom;
    }

    void TextCentred(Graphics g, string text, Font f, Color c, float cx, float baseline) => Text(g, text, f, c, cx - Width(g, text, f) / 2, baseline);

    /// <summary>A row of reading chips, as tall as its chips need.</summary>
    (string?, float, Action<float>) ReadingRow(Graphics g, string label, LcdReading current, int hitBase, float cx, float cw)
    {
        var chips = ChipLayout(g, s_readings, cx, 0, cw, out int chipRows);
        float h = MathF.Max(60 * _s, 32 * _s + chipRows * (ChipH + ChipGap) * _s - ChipGap * _s);
        return (label, h, ry =>
        {
            for (int i = 0; i < chips.Count; i++)
            {
                var chip = chips[i];
                chip.Offset(0, ry + 16 * _s);
                Hits.Add((chip, hitBase + i));
                bool on = (int)current == i;
                FillRound(g, chip, 7 * _s, on ? Raised : Well, White(on ? 30 : 14));
                TextClipped(g, s_readings[i], _fStrong, on || Hover(chip) ? Ink : Muted, chip.X + ChipPad * _s, chip.Y + chip.Height / 2 + 4.5f * _s, chip.Width - 2 * ChipPad * _s + 2);
            }
        });
    }

    /// <summary>Colour presets, then each of the two colours on its own hue strip.</summary>
    void ColourRow(Graphics g, float ry, float cx, float cw, Color c1, Color c2, string name1, string name2)
    {
        float pw = 46 * _s, ph = 22 * _s, pgap = 12 * _s;
        for (int i = 0; i < s_lcdPresets.Length; i++)
        {
            var (_, ca, cb) = s_lcdPresets[i];
            var pill = new RectangleF(cx + i * (pw + pgap), ry + 19 * _s, pw, ph);
            var hit = RectangleF.Inflate(pill, 5 * _s, 5 * _s);
            Hits.Add((hit, HitLcdPreset + i));
            GradientPill(g, pill, ca, cb);
            bool selected = ca.ToArgb() == c1.ToArgb() && cb.ToArgb() == c2.ToArgb();
            if (selected || Hover(hit))
            {
                using var ring = Round(RectangleF.Inflate(pill, 3.5f * _s, 3.5f * _s), ph / 2 + 3.5f * _s);
                using var pen = new Pen(selected ? Ink : White(90), 1.5f * _s);
                g.DrawPath(pen, ring);
            }
        }
        float stripW = MathF.Min(cw - 24 * _s, 5 * pw + 4 * pgap), stripH = 10 * _s;
        _lcdHue1 = new RectangleF(cx + 24 * _s, ry + 64 * _s, stripW, stripH);
        _lcdHue2 = new RectangleF(cx + 24 * _s, ry + 96 * _s, stripW, stripH);
        Text(g, name1, _fNum, Faint, cx, _lcdHue1.Y + stripH / 2 + 4.5f * _s);
        Text(g, name2, _fNum, Faint, cx, _lcdHue2.Y + stripH / 2 + 4.5f * _s);
        Hits.Add((RectangleF.Inflate(_lcdHue1, 9 * _s, 9 * _s), HitLcdHue1));
        Hits.Add((RectangleF.Inflate(_lcdHue2, 9 * _s, 9 * _s), HitLcdHue2));
        Strip(g, _lcdHue1, Spectrum(1, 0.9f), c1.GetHue() / 360f, c1);
        Strip(g, _lcdHue2, Spectrum(1, 0.9f), c2.GetHue() / 360f, c2);
    }

    /// <summary>Two groups of five colour dots, for the left and right readings.</summary>
    void SwatchPair(Graphics g, float cy, float x, Color[] colours, Color left, Color right, int hitBase)
    {
        float d = 18 * _s, pitch = 28 * _s;
        for (int side = 0; side < 2; side++)
        {
            float gx = x + side * (5 * pitch + 44 * _s);
            Text(g, side == 0 ? "L" : "R", _fNum, Faint, gx, cy + 4.5f * _s);
            var current = side == 0 ? left : right;
            for (int i = 0; i < colours.Length; i++)
            {
                var dot = new RectangleF(gx + 20 * _s + i * pitch, cy - d / 2, d, d);
                var hit = RectangleF.Inflate(dot, 5 * _s, 6 * _s);
                Hits.Add((hit, hitBase + side * 5 + i));
                g.FillEllipse(Brush(colours[i]), dot);
                using (var edge = new Pen(White(36), 1)) g.DrawEllipse(edge, dot);
                bool selected = colours[i].ToArgb() == current.ToArgb();
                if (selected || Hover(hit))
                {
                    using var ring = new Pen(selected ? Ink : White(90), 1.5f * _s);
                    g.DrawEllipse(ring, RectangleF.Inflate(dot, 3.5f * _s, 3.5f * _s));
                }
            }
        }
    }

    /// <summary>
    /// The rotation dial: the pump head (Gauge's mark) turned as the user has mounted it, with twelve stops in 30°
    /// steps around it. Gauge turns the picture back by the same amount so it reads upright.
    /// </summary>
    void DialRow(Graphics g, float x, float ry, float h, int dial, float cw)
    {
        float d = 72 * _s, ring = d / 2 + 13 * _s;
        float cx = x + ring + 8 * _s, cy = ry + h / 2;
        var face = new RectangleF(cx - d / 2, cy - d / 2, d, d);
        g.FillEllipse(Brush(Well), face);
        using (var edge = new Pen(White(16), 1)) g.DrawEllipse(edge, face);
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(dial);
        Brand.DrawMark(g, 0, 0, 34 * _s, Accent, White(40), 9);
        g.Restore(state);
        for (int i = 0; i < 12; i++)
        {
            float t = (i * 30 - 90) * MathF.PI / 180;
            var at = new PointF(cx + ring * MathF.Cos(t), cy + ring * MathF.Sin(t));
            var hit = new RectangleF(at.X - 9 * _s, at.Y - 9 * _s, 18 * _s, 18 * _s);
            Hits.Add((hit, HitLcdDial + i));
            bool on = i * 30 == dial, hot = Hover(hit);
            float r = (on ? 4.5f : i % 3 == 0 ? 3 : 2.2f) * _s;
            g.FillEllipse(Brush(on ? Accent : hot ? Ink : i % 3 == 0 ? Muted : Faint), at.X - r, at.Y - r, r * 2, r * 2);
        }
        float tx = cx + ring + 26 * _s, tw = x + cw - tx;
        Text(g, dial == 0 ? "Upright" : $"{dial}°", _fVal, Ink, tx, cy - 4 * _s);
        Paragraph(g, dial == 0 ? "As NZXT CAM sets it. Turn the dial to match how the pump is mounted." : "Pump head turned clockwise by this much; Gauge turns the picture back to stay upright.",
            _fLabel, Muted, tx, cy + 4 * _s, tw, 2);
    }

    /// <summary>The picture library: an add tile, then one round tile per picture, newest first.</summary>
    (string?, float, Action<float>) MediaRow(Graphics g, KrakenLcd screen, LcdSettings s, float cx, float cw)
    {
        var items = KrakenMedia.Items;
        items.Reverse();
        if (items.Count > MaxMediaTiles - 1) items.RemoveRange(MaxMediaTiles - 1, items.Count - (MaxMediaTiles - 1));
        float tile = 64 * _s, gap = 14 * _s;
        int columns = Math.Max(1, (int)((cw + gap) / (tile + gap))), count = items.Count + 1, tileRows = (count + columns - 1) / columns;
        float noteH = 44 * _s, h = 20 * _s + tileRows * tile + (tileRows - 1) * gap + noteH;
        return ("Picture", h, ry =>
        {
            _mediaTiles.Clear();
            for (int i = 0; i < count; i++)
            {
                var r = new RectangleF(cx + i % columns * (tile + gap), ry + 16 * _s + i / columns * (tile + gap), tile, tile);
                bool hot = Hover(r);
                if (i == 0)
                {
                    // Add a picture
                    Hits.Add((r, HitLcdUpload));
                    bool busy = screen.MediaBusy;
                    g.FillEllipse(Brush(hot && !busy ? Raised : Well), r);
                    using (var dash = new Pen(hot ? White(90) : White(40), 1.3f * _s) { DashStyle = DashStyle.Dash }) g.DrawEllipse(dash, RectangleF.Inflate(r, -1, -1));
                    float m = 9 * _s, mx = r.X + r.Width / 2, my = r.Y + r.Height / 2;
                    using var plus = new Pen(hot ? Ink : Muted, 1.6f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(plus, mx - m, my, mx + m, my);
                    g.DrawLine(plus, mx, my - m, mx, my + m);
                    continue;
                }
                var item = items[i - 1];
                int index = _mediaTiles.Count;
                _mediaTiles.Add(item.Id);
                Hits.Add((r, HitLcdMedia + index));
                using (var round = new GraphicsPath())
                {
                    round.AddEllipse(r);
                    var st = g.Save();
                    g.SetClip(round, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (item.Thumb != null) g.DrawImage(item.Thumb, r);
                    else g.FillEllipse(Brush(Well), r);
                    g.Restore(st);
                }
                bool selected = item.Id == s.Media;
                using (var ring = new Pen(selected ? Accent : hot ? White(90) : White(26), (selected ? 2 : 1.2f) * _s))
                    g.DrawEllipse(ring, selected ? RectangleF.Inflate(r, 3 * _s, 3 * _s) : r);
                if (item.Frames > 1)
                {
                    var tag = new RectangleF(r.X + r.Width / 2 - 14 * _s, r.Bottom - 15 * _s, 28 * _s, 14 * _s);
                    FillRound(g, tag, 7 * _s, Color.FromArgb(200, 10, 11, 14));
                    TextCentred(g, "GIF", _fTiny, Soft, tag.X + tag.Width / 2, tag.Bottom - 4 * _s);
                }
                var badge = new RectangleF(r.Right - 16 * _s, r.Y - 2 * _s, 18 * _s, 18 * _s);
                if (hot || Hover(badge))
                {
                    // Delete badge at the top right; added after the tile so it wins
                    Hits.Add((badge, HitLcdMediaDelete + index));
                    bool bhot = Hover(badge);
                    g.FillEllipse(Brush(bhot ? Hot : Raised), badge);
                    using var x = new Pen(Ink, 1.4f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    float k = 4 * _s, bx = badge.X + badge.Width / 2, by = badge.Y + badge.Height / 2;
                    g.DrawLine(x, bx - k, by - k, bx + k, by + k);
                    g.DrawLine(x, bx - k, by + k, bx + k, by - k);
                }
            }

            // A line under the tiles: progress, a refusal, or what is showing
            float ny = ry + 16 * _s + tileRows * tile + (tileRows - 1) * gap + 26 * _s;
            var current = items.Find(i => i.Id == s.Media);
            if (screen.MediaNote is { } note) TextClipped(g, note, _fLabel, screen.MediaNoteIsProblem ? Warn : Muted, cx, ny, cw);
            else if (current != null)
            {
                const string none = "Show the liquid temperature instead";
                float linkW = Width(g, none, _fLabel), nameW = MathF.Max(40 * _s, cw - linkW - 16 * _s);
                TextClipped(g, $"Showing {current.Name}.", _fLabel, Muted, cx, ny, nameW);
                float end = cx + MathF.Min(nameW, Width(g, $"Showing {current.Name}.", _fLabel)) + 10 * _s;
                var link = new RectangleF(end - 4 * _s, ny - 14 * _s, linkW + 8 * _s, 20 * _s);
                Hits.Add((link, HitLcdNoMedia));
                Text(g, none, _fLabel, Hover(link) ? Ink : Faint, end, ny);
            }
            else TextClipped(g, "Add a GIF, PNG or JPEG up to 32 MB. The cooler plays it by itself.", _fLabel, Faint, cx, ny, cw);
        });
    }

    // ---------------------------------------------------------------- crop dialog

    public void BeginCrop(string path, Bitmap image, int frames)
    {
        Crop?.Image.Dispose();
        var box = KrakenMedia.DefaultCrop(image.Size);
        Crop = new CropState { Path = path, Image = image, Frames = frames, Box = box };
    }

    /// <summary>Closes the dialog. Returns the file and its square (whole source pixels) when <paramref name="use"/> is true.</summary>
    public (string Path, Rectangle Square)? EndCrop(bool use)
    {
        var crop = Crop;
        if (crop == null) return null;
        Crop = null;
        var b = crop.Box;
        int side = Math.Max(KrakenMedia.MinCrop, (int)MathF.Round(b.Width));
        var square = new Rectangle(Math.Clamp((int)MathF.Round(b.X), 0, crop.Image.Width - side), Math.Clamp((int)MathF.Round(b.Y), 0, crop.Image.Height - side), side, side);
        crop.Image.Dispose();
        return use ? (crop.Path, square) : null;
    }

    PointF ToSource(CropState c, PointF p) => new((p.X - c.View.X) / c.Scale, (p.Y - c.View.Y) / c.Scale);

    public void CropGrab(PointF p)
    {
        if (Crop is not { } c) return;
        var at = ToSource(c, p);
        c.Grab = new PointF(at.X - c.Box.X, at.Y - c.Box.Y);
    }

    public void CropMove(PointF p)
    {
        if (Crop is not { } c) return;
        var at = ToSource(c, p);
        c.Box.X = Math.Clamp(at.X - c.Grab.X, 0, c.Image.Width - c.Box.Width);
        c.Box.Y = Math.Clamp(at.Y - c.Grab.Y, 0, c.Image.Height - c.Box.Height);
    }

    /// <summary>Mouse wheel: a smaller square zooms in, a larger one out, about the square's centre.</summary>
    public void CropZoom(int delta)
    {
        if (Crop is not { } c) return;
        float most = Math.Min(c.Image.Width, c.Image.Height);
        float side = Math.Clamp(c.Box.Width * (delta > 0 ? 1 / 1.1f : 1.1f), KrakenMedia.MinCrop, most);
        float mx = c.Box.X + c.Box.Width / 2, my = c.Box.Y + c.Box.Height / 2;
        c.Box = new RectangleF(Math.Clamp(mx - side / 2, 0, c.Image.Width - side), Math.Clamp(my - side / 2, 0, c.Image.Height - side), side, side);
    }

    /// <summary>The crop dialog over the whole window: the picture, the square with the round screen inside it, and the buttons.</summary>
    void RenderCrop(Graphics g, float w, float h, CropState c)
    {
        g.FillRectangle(Brush(Color.FromArgb(190, 4, 5, 7)), 0, 0, w, h);
        Hits.Add((new RectangleF(0, ChromeHeight * _s, w, h), HitCropShade)); // blocks the page; the title bar still works
        float pw = MathF.Min(560 * _s, w - 64 * _s), ph = MathF.Min(600 * _s, h - ChromeHeight * _s - 32 * _s);
        var panel = new RectangleF((w - pw) / 2, ChromeHeight * _s + (h - ChromeHeight * _s - ph) / 2, pw, ph);
        Plate(g, panel);
        float p = 24 * _s;
        Text(g, "Crop for the round screen", _fSection, Ink, panel.X + p, panel.Y + 38 * _s);
        string about = $"{Path.GetFileName(c.Path)}  ·  {c.Image.Width} × {c.Image.Height}" + (c.Frames > 1 ? $"  ·  {c.Frames} frames" : "");
        TextClipped(g, about, _fLabel, Muted, panel.X + p, panel.Y + 60 * _s, pw - 2 * p);

        float side = MathF.Max(80 * _s, MathF.Min(pw - 2 * p, ph - 150 * _s));
        var viewport = new RectangleF(panel.X + (pw - side) / 2, panel.Y + 76 * _s, side, side);
        FillRound(g, viewport, 8 * _s, Well);
        float scale = side / Math.Max(c.Image.Width, c.Image.Height);
        var view = new RectangleF(viewport.X + (side - c.Image.Width * scale) / 2, viewport.Y + (side - c.Image.Height * scale) / 2, c.Image.Width * scale, c.Image.Height * scale);
        c.View = view;
        c.Scale = scale;
        var st = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(c.Image, view);
        var box = new RectangleF(view.X + c.Box.X * scale, view.Y + c.Box.Y * scale, c.Box.Width * scale, c.Box.Height * scale);
        using (var outside = new GraphicsPath(FillMode.Alternate))
        {
            outside.AddRectangle(view);
            outside.AddEllipse(box);
            g.FillPath(Brush(Color.FromArgb(160, 0, 0, 0)), outside); // all but the round screen is dimmed
        }
        using (var square = new Pen(White(70), 1)) g.DrawRectangle(square, box.X, box.Y, box.Width, box.Height);
        using (var round = new Pen(Accent, 1.6f * _s)) g.DrawEllipse(round, box);
        g.Restore(st);
        Hits.Add((box, HitCropBox));

        float by = panel.Bottom - 34 * _s;
        Text(g, "Drag to move  ·  Scroll to zoom", _fLabel, Faint, panel.X + p, by + 4.5f * _s);
        float left = Button(g, "Use this crop", HitCropUse, panel.Right - p, by, Accent);
        Button(g, "Cancel", HitCropCancel, left - 8 * _s, by, default);
    }
}
