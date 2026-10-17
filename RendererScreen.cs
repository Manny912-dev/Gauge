using System.Drawing.Drawing2D;

namespace Gauge;

// The Kraken's screen on the Lighting page: a list entry and an editor with a live preview.
internal sealed partial class Renderer
{
    // Hit ids, in the free range above the settings page's
    public const int HitLcdItem = 130, HitLcdGauge = 131, HitLcdOwn = 132, HitLcdReading = 133, // 133..139
        HitLcdStyle = 140, HitLcdPreset = 142, HitLcdHue1 = 148, HitLcdHue2 = 149,              // 140..141, 142..146
        HitLcdBright = 150, HitLcdRotate = 154, HitLcdLogo = 158;                              // 150..153, 154..157

    static readonly (string Name, Color A, Color B)[] s_lcdPresets =
    [
        ("Violet", Rgb(0x6B00DE), Rgb(0xD600BF)), ("Amber", Rgb(0xFFA53C), Rgb(0xFF4D4D)), ("Ocean", Rgb(0x0057FF), Rgb(0x00D0FF)),
        ("Lime", Rgb(0x00B37E), Rgb(0xB8E04A)), ("Mono", Rgb(0x8E96A3), Rgb(0xFFFFFF)),
    ];
    static readonly string[] s_readings = ["Liquid", "CPU temp", "CPU load", "CPU clock", "GPU temp", "GPU load", "GPU clock"];
    static readonly int[] s_lcdBrightness = [25, 50, 75, 100];
    static readonly int[] s_lcdRotation = [0, 90, 180, 270];

    bool _screenSelected, _screenShown;
    RectangleF _lcdHue1, _lcdHue2;

    /// <summary>Handles a click or drag on the screen's controls. Settings are saved and the frame redrawn by the screen itself.</summary>
    public void ScreenPress(int id, float x)
    {
        var screen = Lights?.Screen;
        if (id == HitLcdItem) { _screenSelected = true; return; }
        if (screen == null) return;
        switch (id)
        {
            case HitLcdGauge or HitLcdOwn: screen.Change(s => s.Drive = id == HitLcdGauge); break;
            case >= HitLcdReading and < HitLcdReading + 7: screen.Change(s => s.Reading = (LcdReading)(id - HitLcdReading)); break;
            case HitLcdStyle or HitLcdStyle + 1: screen.Change(s => s.Fill = id == HitLcdStyle + 1); break;
            case >= HitLcdPreset and < HitLcdPreset + 5:
                var p = s_lcdPresets[id - HitLcdPreset];
                screen.Change(s => { s.Color1 = p.A; s.Color2 = p.B; });
                break;
            case HitLcdHue1 or HitLcdHue2:
                var strip = id == HitLcdHue1 ? _lcdHue1 : _lcdHue2;
                float hue = Math.Clamp((x - strip.X) / MathF.Max(1, strip.Width), 0, 1) * 359.9f;
                var c = FromHsv(hue, 1, 0.9f);
                screen.Change(s => { if (id == HitLcdHue1) s.Color1 = c; else s.Color2 = c; });
                break;
            case >= HitLcdBright and < HitLcdBright + 4: screen.Change(s => s.Brightness = s_lcdBrightness[id - HitLcdBright]); break;
            case >= HitLcdRotate and < HitLcdRotate + 4: screen.Change(s => s.Rotation = s_lcdRotation[id - HitLcdRotate]); break;
            case HitLcdLogo: screen.Change(s => s.Logo = !s.Logo); break;
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
        string sub = screen.Problem ? (screen.Status.StartsWith("NZXT CAM") ? "Close NZXT CAM to drive it" : "Needs attention") : s.Drive ? "Screen  ·  Gauge display" : "Screen  ·  Kraken's own";
        TextClipped(g, sub, _fLabel, screen.Problem && s.Drive ? Warn : Muted, tx, cy + 15 * _s, tw);
        GradientPill(g, new RectangleF(row.Right - 16 * _s - sw, cy - 6 * _s, sw, 12 * _s), s.Drive ? s.Color1 : Rgb(0x1A1C20), s.Drive ? s.Color2 : Rgb(0x1A1C20));
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

        // Stage: the round screen as it looks, beside what Gauge knows about the cooler
        var stage = new RectangleF(a.X, y + 24 * _s, a.Width, 244 * _s);
        Plate(g, stage);
        float size = 204 * _s, p = 20 * _s;
        var circle = new RectangleF(stage.X + p, stage.Y + (stage.Height - size) / 2, size, size);
        using (var glow = new GraphicsPath())
        {
            var halo = RectangleF.Inflate(circle, 26 * _s, 26 * _s);
            glow.AddEllipse(halo);
            using var brush = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(s.Drive ? 46 : 0, s.Color1), SurroundColors = [Color.FromArgb(0, s.Color1)] };
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
            lock (screen.Sync)
            {
                if (screen.Preview is { } frame) g.DrawImage(frame, circle);
                else g.FillEllipse(Brush(Rgb(0x050506)), circle);
            }
            g.Restore(state);
            using var bezel = new Pen(White(28), 1.5f * _s);
            g.DrawEllipse(bezel, circle);
        }

        float tx = circle.Right + 30 * _s, tw = stage.Right - p - tx;
        TextClipped(g, screen.Name, _fSection, Ink, tx, stage.Y + 46 * _s, tw);
        float sy = Paragraph(g, screen.Status, _fLabel, screen.Problem && s.Drive ? Warn : Muted, tx, stage.Y + 56 * _s, tw, 3);
        if (!s.Drive) Paragraph(g, "Choose Gauge below to show this design on the cooler. Gauge then holds the cooler, so NZXT CAM has to stay closed.", _fLabel, Faint, tx, sy + 8 * _s, tw, 4);
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

        // Settings, as labelled rows
        var area = new RectangleF(a.X, stage.Bottom + 16 * _s, a.Width, 0);
        float lp = 18 * _s, labelW = 104 * _s, cx = area.X + lp + labelW, right = area.Right - lp, cw = right - cx;
        var chips = ChipLayout(g, s_readings, cx, 0, cw, out int chipRows);
        float showsH = MathF.Max(60 * _s, 32 * _s + chipRows * (ChipH + ChipGap) * _s - ChipGap * _s);
        float rowH = 60 * _s, colourH = 128 * _s;
        var plate = new RectangleF(area.X, area.Y, area.Width, rowH + showsH + rowH + colourH + rowH + rowH + rowH);
        Plate(g, plate);
        float ry = plate.Y;

        Text(g, "Screen", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        Segmented(g, cx, ry + 13 * _s, ["Gauge", "Kraken's own"], s.Drive ? 0 : 1, HitLcdGauge, true);
        ry += rowH;
        Hairline(g, plate.X + lp, right, ry, 10);

        Text(g, "Shows", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        for (int i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            chip.Offset(0, ry + 16 * _s);
            Hits.Add((chip, HitLcdReading + i));
            bool on = (int)s.Reading == i;
            FillRound(g, chip, 7 * _s, on ? Raised : Well, White(on ? 30 : 14));
            TextClipped(g, s_readings[i], _fStrong, on || Hover(chip) ? Ink : Muted, chip.X + ChipPad * _s, chip.Y + chip.Height / 2 + 4.5f * _s, chip.Width - 2 * ChipPad * _s + 2);
        }
        ry += showsH;
        Hairline(g, plate.X + lp, right, ry, 10);

        Text(g, "Style", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        Segmented(g, cx, ry + 13 * _s, ["Ring", "Fill"], s.Fill ? 1 : 0, HitLcdStyle, true);
        ry += rowH;
        Hairline(g, plate.X + lp, right, ry, 10);

        // Colours: presets, then each end of the gradient on its own hue strip
        Text(g, "Colours", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        float pw = 46 * _s, ph = 22 * _s, pgap = 12 * _s;
        for (int i = 0; i < s_lcdPresets.Length; i++)
        {
            var (_, ca, cb) = s_lcdPresets[i];
            var pill = new RectangleF(cx + i * (pw + pgap), ry + 19 * _s, pw, ph);
            var hit = RectangleF.Inflate(pill, 5 * _s, 5 * _s);
            Hits.Add((hit, HitLcdPreset + i));
            GradientPill(g, pill, ca, cb);
            bool selected = ca.ToArgb() == s.Color1.ToArgb() && cb.ToArgb() == s.Color2.ToArgb();
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
        Text(g, "1", _fNum, Faint, cx, _lcdHue1.Y + stripH / 2 + 4.5f * _s);
        Text(g, "2", _fNum, Faint, cx, _lcdHue2.Y + stripH / 2 + 4.5f * _s);
        Hits.Add((RectangleF.Inflate(_lcdHue1, 9 * _s, 9 * _s), HitLcdHue1));
        Hits.Add((RectangleF.Inflate(_lcdHue2, 9 * _s, 9 * _s), HitLcdHue2));
        Strip(g, _lcdHue1, Spectrum(1, 0.9f), s.Color1.GetHue() / 360f, s.Color1);
        Strip(g, _lcdHue2, Spectrum(1, 0.9f), s.Color2.GetHue() / 360f, s.Color2);
        ry += colourH;
        Hairline(g, plate.X + lp, right, ry, 10);

        Text(g, "Brightness", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        int bi = Array.FindIndex(s_lcdBrightness, b => b >= s.Brightness);
        Segmented(g, cx, ry + 13 * _s, ["25%", "50%", "75%", "100%"], bi < 0 ? 3 : bi, HitLcdBright, true);
        ry += rowH;
        Hairline(g, plate.X + lp, right, ry, 10);

        Text(g, "Rotation", _fBody, Muted, plate.X + lp, ry + 34.5f * _s);
        Segmented(g, cx, ry + 13 * _s, ["Upright", "90°", "180°", "270°"], Array.IndexOf(s_lcdRotation, s.Rotation), HitLcdRotate, true);
        ry += rowH;
        Hairline(g, plate.X + lp, right, ry, 10);

        SwitchRow(g, new RectangleF(plate.X, ry, plate.Width, rowH), "Show the Gauge mark", "Above the number, where NZXT CAM puts its logo", s.Logo, HitLcdLogo);
        return plate.Bottom;
    }
}
