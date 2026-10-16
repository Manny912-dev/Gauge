using System.Drawing.Drawing2D;

namespace Gauge;

// The Lighting page. Kept apart from the monitoring pages so lighting work cannot disturb them.
internal sealed partial class Renderer
{
    // Hit ids, all inside the clickable range below HitSwatch.
    public const int HitLighting = 16, HitBoard = 18, HitBoardOff = 19,
        HitZone = 30, HitZoneAll = 38, HitLightPreset = 40, HitLightHue = 50, HitLightSat = 51,
        HitEffect = 60, MaxEffects = 20, // 60..79
        HitMatchAccent = 80, HitRestore = 81, HitRescan = 82, HitCopyDevices = 83,
        HitBright = 84, HitSpeed = 87, HitLightDevice = 91, MaxDeviceChips = 9; // 84..86, 87..90, 91..99

    const int MaxShownZones = 8;

    static readonly Color[] LightPresets =
        [Rgb(0xFFFFFF), Rgb(0xFF0000), Rgb(0xFF7A00), Rgb(0xFFD400), Rgb(0x00E05A), Rgb(0x00D0FF), Rgb(0x2A5BFF), Rgb(0xE000FF)];

    public Lighting? Lights;
    /// <summary>Set while the motherboard and RAM setup runs; the result shows for a while afterwards.</summary>
    public bool BoardBusy;
    public string? BoardNote;
    public long BoardNoteUntil;
    public RectangleF LightHueRect, LightSatRect;

    readonly bool[] _lightSel = Enumerable.Repeat(true, 64).ToArray();
    float _lightHue = 30;
    bool _lightHueSynced;
    int _lightIndex;

    /// <summary>The device the page is showing. Caller holds <c>Lights.Sync</c>.</summary>
    LightDevice? LightTarget => Lights is { Scanned: true } l && l.Devices.Count > 0 ? l.Devices[Math.Clamp(_lightIndex, 0, l.Devices.Count - 1)] : null;

    /// <summary>The device the page is showing, for the window to apply and save.</summary>
    public LightDevice? SelectedLight
    {
        get
        {
            if (Lights == null) return null;
            lock (Lights.Sync) return LightTarget;
        }
    }

    static void ToHsv(Color c, out float hue, out float saturation)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        saturation = max == 0 ? 0 : (max - min) / (float)max;
        hue = c.GetHue();
    }

    int ZoneCount(LightDevice d) => Math.Min(d.State.Zones.Length, _lightSel.Length);

    Color PickerColour(LightDevice d)
    {
        for (int i = 0; i < ZoneCount(d); i++) if (_lightSel[i]) return d.State.Zones[i];
        return d.State.Zones.Length > 0 ? d.State.Zones[0] : Color.White;
    }

    bool AllSelected(LightDevice d)
    {
        for (int i = 0; i < ZoneCount(d); i++) if (!_lightSel[i]) return false;
        return true;
    }

    void SetColour(LightDevice d, Color c)
    {
        for (int i = 0; i < ZoneCount(d); i++) if (_lightSel[i]) d.State.Zones[i] = c;
        // Choosing a colour means wanting to see it: leave self-colouring effects and "off".
        if (!d.ModeUsesColours(d.State.Mode)) d.State.Mode = d.ColourMode;
        if (d.State.Brightness == 0) d.State.Brightness = 2;
        ToHsv(c, out float hue, out float sat);
        if (sat > 0.02f) _lightHue = hue;
    }

    /// <summary>Handles a click on the page. True when the device's lighting changed and should be sent.</summary>
    public bool LightingPress(int id, float x)
    {
        if (Lights == null) return false;
        lock (Lights.Sync)
        {
            if (id is >= HitLightDevice and < HitLightDevice + MaxDeviceChips)
            {
                _lightIndex = id - HitLightDevice;
                Array.Fill(_lightSel, true);
                _lightHueSynced = false;
                return false;
            }
            var d = LightTarget;
            if (d == null) return false;
            int zones = ZoneCount(d);
            switch (id)
            {
                case >= HitZone and < HitZoneAll when id - HitZone < zones:
                    bool only = true;
                    for (int i = 0; i < zones; i++) only &= _lightSel[i] == (i == id - HitZone);
                    for (int i = 0; i < zones; i++) _lightSel[i] = only || i == id - HitZone; // a second click on a lone zone selects all
                    ToHsv(PickerColour(d), out float h, out float s);
                    if (s > 0.02f) _lightHue = h;
                    return false;
                case HitZoneAll:
                    for (int i = 0; i < zones; i++) _lightSel[i] = true;
                    return false;
                case >= HitLightPreset and < HitLightPreset + 8:
                    SetColour(d, LightPresets[id - HitLightPreset]);
                    return true;
                case HitLightHue or HitLightSat:
                    return DragLocked(d, id, x);
                case >= HitEffect and < HitEffect + MaxEffects when id - HitEffect < d.Modes.Length:
                    d.State.Mode = id - HitEffect;
                    if (d.State.Brightness == 0) d.State.Brightness = 2;
                    return true;
                case >= HitBright and < HitBright + 3:
                    d.State.Brightness = id - HitBright;
                    return true;
                case >= HitSpeed and < HitSpeed + 4:
                    d.State.Speed = id - HitSpeed + 1;
                    return true;
                case HitMatchAccent:
                    SetColour(d, Accent);
                    return true;
                case HitRestore:
                    Lights.RestoreOnLaunch = !Lights.RestoreOnLaunch;
                    return false;
            }
            return false;
        }
    }

    /// <summary>Handles a drag along the hue or saturation strip. True when the lighting changed.</summary>
    public bool LightingDrag(int id, float x)
    {
        if (Lights == null) return false;
        lock (Lights.Sync)
        {
            var d = LightTarget;
            return d != null && DragLocked(d, id, x);
        }
    }

    bool DragLocked(LightDevice d, int id, float x)
    {
        var strip = id == HitLightHue ? LightHueRect : LightSatRect;
        float f = Math.Clamp((x - strip.X) / MathF.Max(1, strip.Width), 0, 1);
        ToHsv(PickerColour(d), out _, out float sat);
        if (id == HitLightHue)
        {
            _lightHue = f * 359.9f;
            SetColour(d, FromHsv(_lightHue, sat < 0.05f ? 1 : sat, 1)); // dragging hue from white should give a colour
        }
        else
        {
            SetColour(d, FromHsv(_lightHue, f, 1));
        }
        return true;
    }

    // ---------------------------------------------------------------- drawing

    // The page is a list and an inspector: devices (and the hardware that needs setting up) on the
    // left, everything about the chosen device on the right.

    void RenderLighting(Graphics g, RectangleF a)
    {
        var lights = Lights;
        if (lights == null || !lights.Scanned)
        {
            Text(g, "Looking for lighting devices…", _fBody, Muted, a.X, a.Y + 30 * _s);
            return;
        }
        lock (lights.Sync)
        {
            if (_lightIndex >= lights.Devices.Count) { _lightIndex = 0; Array.Fill(_lightSel, true); _lightHueSynced = false; }
            var d = LightTarget;
            // Laid out at full height and scrolled when the window is too short for it.
            var state = g.Save();
            g.SetClip(RectangleF.FromLTRB(RailWidth * _s + 1, a.Y - 2 * _s, a.Right + 20 * _s, a.Bottom + 18 * _s));
            var view = a;
            a.Y -= Scroll;
            float listW = Math.Clamp(a.Width * 0.3f, 226 * _s, 280 * _s);
            float bottom = DeviceColumn(g, new RectangleF(a.X, a.Y, listW, a.Height), lights);
            var right = RectangleF.FromLTRB(a.X + listW + 18 * _s, a.Y, a.Right, a.Bottom);
            bottom = MathF.Max(bottom, d == null ? NoLighting(g, right, lights) : Editor(g, right, d, lights));
            g.Restore(state);
            MaxScroll = MathF.Max(0, bottom + Scroll - view.Bottom);
            if (Scroll > MaxScroll) Scroll = MaxScroll;
        }
    }

    // ---- left column

    float DeviceColumn(Graphics g, RectangleF a, Lighting lights)
    {
        float y = a.Y + 6 * _s;
        float top = GroupHeading(g, "Devices", a.X, y);
        if (lights.EngineBusy) TextRight(g, "Scanning…", _fLabel, Faint, a.Right, y + 12 * _s);

        int n = Math.Min(lights.Devices.Count, MaxDeviceChips);
        float itemH = 58 * _s;
        var plate = new RectangleF(a.X, top, a.Width, Math.Max(1, n) * itemH);
        Plate(g, plate);
        if (n == 0) RowText(g, "None yet", "Supported hardware shows up here", plate.X + 16 * _s, plate.Y, plate.Width - 32 * _s, itemH);
        int selected = Math.Clamp(_lightIndex, 0, Math.Max(0, lights.Devices.Count - 1));
        for (int i = 0; i < n; i++) DeviceItem(g, new RectangleF(plate.X, plate.Y + i * itemH, plate.Width, itemH), lights.Devices[i], i, i == selected);
        y = plate.Bottom + 12 * _s;

        if (lights.EngineProblem != null)
            y = Paragraph(g, lights.EngineProblem, _fLabel, Warn, a.X + 2 * _s, y, a.Width - 4 * _s, 3) + 4 * _s;

        y = BoardGroup(g, a, y + 12 * _s, lights);
        return UnclaimedGroup(g, a, y + 12 * _s, lights);
    }

    void DeviceItem(Graphics g, RectangleF row, LightDevice d, int index, bool on)
    {
        Hits.Add((row, HitLightDevice + index));
        bool hot = Hover(row);
        if (index > 0) Hairline(g, row.X + 14 * _s, row.Right - 14 * _s, row.Y, 10);
        if (on || hot) FillRound(g, RectangleF.Inflate(row, -5 * _s, -4 * _s), 8 * _s, White(on ? 10 : 5));
        if (on) Pill(g, new RectangleF(row.X + 9 * _s, row.Y + row.Height / 2 - 10 * _s, 3 * _s, 20 * _s), Accent);

        float cy = row.Y + row.Height / 2, x = row.X + 22 * _s;
        Icon(g, KindIcon(d.Kind), x, cy - 8 * _s, on ? Ink : Muted, 16);
        float sw = 30 * _s, tx = x + 28 * _s, tw = row.Right - 16 * _s - sw - 12 * _s - tx;
        TextClipped(g, d.Name, _fStrong, on || hot ? Ink : Soft, tx, cy - 2 * _s, tw);
        int zones = d.State.Zones.Length;
        if (d.Error != null) TextClipped(g, "Not responding", _fLabel, Hot, tx, cy + 15 * _s, tw);
        else TextClipped(g, zones == 1 ? $"{d.Kind}" : $"{d.Kind}  ·  {zones} zones", _fLabel, Muted, tx, cy + 15 * _s, tw);
        Swatch(g, new RectangleF(row.Right - 16 * _s - sw, cy - 6 * _s, sw, 12 * _s), d);
    }

    /// <summary>A small pill showing what the device is lit with: its zones, its effect's spectrum, or dark when off.</summary>
    void Swatch(Graphics g, RectangleF r, LightDevice d)
    {
        var s = d.State;
        using var path = Round(r, r.Height / 2);
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        if (s.Brightness == 0 || s.Zones.Length == 0) g.FillRectangle(Brush(Rgb(0x1A1C20)), r);
        else if (!d.ModeUsesColours(s.Mode))
        {
            using var spectrum = new LinearGradientBrush(new PointF(r.X - 1, 0), new PointF(r.Right + 1, 0), Color.Black, Color.Black) { InterpolationColors = Spectrum(1, s.Brightness == 1 ? 0.55f : 1) };
            g.FillRectangle(spectrum, r);
        }
        else
        {
            int n = Math.Min(s.Zones.Length, MaxShownZones);
            for (int i = 0; i < n; i++)
            {
                float a = r.X + r.Width * i / n, b = r.X + r.Width * (i + 1) / n;
                g.FillRectangle(Brush(Shown(s, i)), a, r.Y, b - a + 0.5f, r.Height);
            }
        }
        g.Restore(state);
        using var edge = new Pen(White(30), 1);
        g.DrawPath(edge, path);
    }

    static string KindIcon(string kind) => kind switch
    {
        "Keyboard" => "Keyboard",
        "Mouse" => "Mouse",
        "Headset" => "Headset",
        "Memory" => "Memory",
        "Graphics card" => "Graphics",
        "Motherboard" => "Processor",
        "Storage" => "Storage",
        "Monitor" => "Display",
        _ => "Lighting",
    };

    /// <summary>Motherboard and RAM lighting: its state, and the one button that changes it.</summary>
    float BoardGroup(Graphics g, RectangleF a, float y, Lighting lights)
    {
        float top = GroupHeading(g, "Motherboard & RAM", a.X, y);
        float p = 16 * _s, x = a.X + p, w = a.Width - 2 * p;

        bool note = BoardNote != null && Environment.TickCount64 < BoardNoteUntil;
        string title, body;
        string? action = null;
        int id = HitBoard;
        bool on = false;
        if (BoardBusy) { title = "Setting up"; body = "Approve the administrator prompt to continue."; }
        else if (!lights.BoardEnabled) { title = "Off"; body = "Turning it on installs the PawnIO driver, with one administrator approval."; action = "Turn on"; }
        else if (lights.BoardNeedsRefresh) { title = "Update ready"; body = "The protected lighting engine has a newer version."; action = "Update"; on = true; }
        else { title = "On"; body = "Runs with administrator rights from Program Files."; action = "Turn off"; id = HitBoardOff; on = true; }
        if (note) body = BoardNote!;

        // The plate grows with its sentence, up to three lines, so nothing is cut off in a narrow window.
        float bodyH = MathF.Min(g.MeasureString(body, _fLabel, new SizeF(w, 1000), _sfWrap).Height, _fLabel.GetHeight(g) * 3);
        var plate = new RectangleF(a.X, top, a.Width, 34 * _s + bodyH + (action != null ? 52 * _s : 14 * _s));
        Plate(g, plate);
        float dot = 3 * _s;
        g.FillEllipse(Brush(on ? Accent : Faint), x, plate.Y + 26 * _s - 4.5f * _s - dot, dot * 2, dot * 2);
        TextClipped(g, title, _fStrong, Ink, x + 13 * _s, plate.Y + 26 * _s, w - 13 * _s);
        Paragraph(g, body, _fLabel, Muted, x, plate.Y + 34 * _s, w, 3);
        if (action != null) Button(g, action, id, plate.Right - p, plate.Bottom - 25 * _s, id == HitBoardOff ? default : Accent);
        return plate.Bottom;
    }

    /// <summary>Hardware that may have lighting but has no driver yet, with a way to send its details back.</summary>
    float UnclaimedGroup(Graphics g, RectangleF a, float y, Lighting lights)
    {
        var list = lights.Unclaimed;
        float rowH = 46 * _s;
        if (list.Count == 0) return y;
        float top = GroupHeading(g, "No driver yet", a.X, y);
        Text(g, list.Count.ToString(), _fUnit, Faint, a.X + CapsWidth(g, "No driver yet") + 9 * _s, y + 12 * _s);
        string copy = Environment.TickCount64 < CopiedUntil ? "Copied" : "Copy report";
        float cw = Width(g, copy, _fLabel);
        var link = new RectangleF(a.Right - cw - 6 * _s, y - 4 * _s, cw + 12 * _s, 22 * _s);
        Hits.Add((link, HitCopyDevices));
        TextRight(g, copy, _fLabel, Hover(link) ? Ink : Muted, a.Right, y + 12 * _s);

        int rows = Math.Min(list.Count, 5);
        var plate = new RectangleF(a.X, top, a.Width, rows * rowH);
        Plate(g, plate);
        float x = plate.X + 16 * _s, right = plate.Right - 16 * _s;
        for (int i = 0; i < rows; i++)
        {
            var u = list[i];
            float ry = plate.Y + i * rowH;
            if (i > 0) Hairline(g, x, right, ry, 10);
            bool last = i == rows - 1 && rows < list.Count;
            string more = last ? $"+{list.Count - rows} more" : "";
            float mw = Width(g, more, _fLabel);
            TextClipped(g, u.Product, _fBody, Soft, x, ry + 20 * _s, right - x - (last ? mw + 10 * _s : 0));
            TextClipped(g, $"{u.Vendor}  ·  {u.VendorId:X4}:{u.ProductId:X4}", _fLabel, Faint, x, ry + 37 * _s, right - x);
            if (last) TextRight(g, more, _fLabel, Faint, right, ry + 20 * _s);
        }
        return plate.Bottom;
    }

    // ---- right side

    float NoLighting(Graphics g, RectangleF a, Lighting lights)
    {
        float top = GroupHeading(g, "Lighting", a.X, a.Y + 6 * _s);
        var plate = new RectangleF(a.X, top, a.Width, 150 * _s);
        Plate(g, plate);
        float x = plate.X + 22 * _s, w = plate.Width - 44 * _s;
        if (lights.EngineBusy)
        {
            Text(g, "Looking for lighting devices", _fSection, Ink, x, plate.Y + 40 * _s);
            Paragraph(g, "Gauge is checking every connected device. This takes a few seconds.", _fBody, Muted, x, plate.Y + 54 * _s, w, 2);
            return plate.Bottom;
        }
        Text(g, "No supported lighting devices found", _fSection, Ink, x, plate.Y + 40 * _s);
        float b = Paragraph(g, "Gauge drives lighting with its own drivers and a built-in OpenRGB engine, so no vendor app is needed.", _fBody, Muted, x, plate.Y + 54 * _s, w, 2);
        Paragraph(g, "If your RGB hardware is not listed, copy the report under \"No driver yet\" and send it in so support can be added.", _fLabel, Faint, x, b + 8 * _s, w, 2);
        return plate.Bottom;
    }

    float Editor(Graphics g, RectangleF a, LightDevice d, Lighting lights)
    {
        float y = a.Y + 6 * _s;
        float labelEnd = Caps(g, d.Kind, a.X, y + 12 * _s, Muted);
        TextRightClipped(g, d.Detail, _fLabel, Faint, labelEnd + 20 * _s, a.Right, y + 12 * _s);

        float stageH = Math.Clamp(a.Height - 388 * _s, 116 * _s, 152 * _s);
        var stage = new RectangleF(a.X, y + 24 * _s, a.Width, stageH);
        Stage(g, stage, d);

        // What the person should know about the hardware right now
        float ny = stage.Bottom + 20 * _s;
        var s = d.State;
        string extra = s.Zones.Length > MaxShownZones ? $"  Showing {MaxShownZones} of {s.Zones.Length} zones." : "";
        if (d.Error != null) TextClipped(g, $"Could not reach the device: {d.Error}", _fLabel, Hot, a.X + 2 * _s, ny, a.Width);
        else if (lights.Rival != null) TextClipped(g, $"{lights.Rival} is running and may override what Gauge sets.", _fLabel, Warn, a.X + 2 * _s, ny, a.Width);
        else TextClipped(g, d.Note + extra, _fLabel, Faint, a.X + 2 * _s, ny, a.Width);

        return Inspector(g, new RectangleF(a.X, ny + 14 * _s, a.Width, 0), d, lights);
    }

    /// <summary>The device drawn as its zones, lit: each zone glows into the plate in its colour.</summary>
    void Stage(Graphics g, RectangleF r, LightDevice d)
    {
        Plate(g, r);
        var s = d.State;
        float p = 20 * _s;
        TextClipped(g, d.Name, _fSection, Ink, r.X + p, r.Y + 32 * _s, r.Width * 0.6f);
        bool all = AllSelected(d);
        if (all) TextRight(g, "All zones", _fLabel, Faint, r.Right - p, r.Y + 31 * _s);
        else
        {
            const string selectAll = "Select all zones";
            float aw = Width(g, selectAll, _fLabel);
            var link = new RectangleF(r.Right - p - aw - 6 * _s, r.Y + 15 * _s, aw + 12 * _s, 22 * _s);
            Hits.Add((link, HitZoneAll));
            TextRight(g, selectAll, _fLabel, Hover(link) ? Ink : Muted, r.Right - p, r.Y + 31 * _s);
        }

        int zones = Math.Min(MaxShownZones, s.Zones.Length);
        if (zones == 0) return;
        bool colours = d.ModeUsesColours(s.Mode), lit = s.Brightness > 0;
        float zgap = 10 * _s, zTop = r.Y + 50 * _s, labelY = r.Bottom - 18 * _s, zh = MathF.Max(20 * _s, labelY - 20 * _s - zTop);
        float zw = MathF.Min(150 * _s, (r.Width - 2 * p - (zones - 1) * zgap) / zones);
        float total = zones * zw + (zones - 1) * zgap, zx = r.X + (r.Width - total) / 2;

        // Light first, so every zone sits on the glow of its neighbours too
        var state = g.Save();
        using (var clip = Round(Snap(r), PlateRadius * _s)) g.SetClip(clip, CombineMode.Intersect);
        if (lit)
        {
            for (int i = 0; i < zones; i++)
            {
                var z = new RectangleF(zx + i * (zw + zgap), zTop, zw, zh);
                Color c = colours ? Shown(s, i) : FromHsv(360f * (i + 0.5f) / zones, 1, s.Brightness == 1 ? 0.55f : 1);
                var glow = RectangleF.Inflate(z, zw * 0.28f + 10 * _s, zh * 0.5f + 16 * _s);
                using var path = new GraphicsPath();
                path.AddEllipse(glow);
                using var brush = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(s.Brightness == 1 ? 46 : 78, c),
                    SurroundColors = [Color.FromArgb(0, c)],
                    CenterPoint = new PointF(z.X + z.Width / 2, z.Y + z.Height * 0.6f),
                };
                g.FillPath(brush, path);
            }
        }
        g.Restore(state);

        for (int i = 0; i < zones; i++)
        {
            var z = new RectangleF(zx + i * (zw + zgap), zTop, zw, zh);
            using (var path = Round(z, 9 * _s))
            {
                if (lit && !colours)
                {
                    // Effects that colour themselves are shown as the spectrum they run through.
                    using var spectrum = new LinearGradientBrush(new PointF(zx, 0), new PointF(zx + total, 0), Color.Black, Color.Black) { InterpolationColors = Spectrum(1, s.Brightness == 1 ? 0.55f : 1) };
                    g.FillPath(spectrum, path);
                }
                else g.FillPath(Brush(Shown(s, i)), path);
                if (lit)
                {
                    // A soft sheen across the top, as light through a keycap or diffuser
                    using var sheen = new LinearGradientBrush(new PointF(0, z.Y - 1), new PointF(0, z.Bottom + 1), White(44), White(0))
                    {
                        Blend = new Blend { Positions = [0f, 0.55f, 1f], Factors = [0f, 1f, 1f] },
                    };
                    g.FillPath(sheen, path);
                }
            }

            bool selected = _lightSel[i];
            var hit = RectangleF.FromLTRB(z.X - zgap / 2, z.Y - 6 * _s, z.Right + zgap / 2, labelY + 6 * _s);
            Hits.Add((hit, HitZone + i));
            bool hot = Hover(hit);
            // Rings only mark a partial choice; with every zone chosen they would just be noise.
            bool ringed = selected && !all;
            if (ringed || hot)
            {
                using var ring = Round(RectangleF.Inflate(z, 3.5f * _s, 3.5f * _s), 12 * _s);
                using var pen = new Pen(ringed ? Ink : White(70), 1.6f * _s);
                g.DrawPath(pen, ring);
            }

            string name = i < d.ZoneNames.Length && d.ZoneNames[i].Length > 0 ? d.ZoneNames[i] : $"Zone {i + 1}";
            if (Width(g, name, _fLabel) > zw + zgap - 4 * _s) name = $"Zone {i + 1}";
            if (Width(g, name, _fLabel) > zw + zgap - 4 * _s) name = (i + 1).ToString();
            TextCenter(g, name, _fLabel, ringed || (all && lit) ? Ink : hot ? Soft : Faint, z.X + zw / 2, labelY);
        }
    }

    /// <summary>The colour a zone is showing, as the page previews it.</summary>
    static Color Shown(LightState s, int zone)
    {
        if (s.Brightness == 0) return Rgb(0x1A1C20);
        var c = s.Zones[zone];
        float k = s.Brightness == 1 ? 0.55f : 1;
        return Color.FromArgb(255, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }

    static ColorBlend Spectrum(float saturation, float value)
    {
        var blend = new ColorBlend(13);
        for (int i = 0; i < 13; i++)
        {
            blend.Positions[i] = i / 12f;
            blend.Colors[i] = FromHsv(MathF.Min(359.9f, i * 30), saturation, value);
        }
        return blend;
    }

    const float ChipH = 28, ChipGap = 6, ChipPad = 12;

    /// <summary>Lays out wrapping option chips. Returns each chip's rectangle and the number of rows.</summary>
    List<RectangleF> ChipLayout(Graphics g, IReadOnlyList<string> labels, float x, float y, float width, out int rows)
    {
        var rects = new List<RectangleF>();
        float cx = x, cy = y, h = ChipH * _s, gap = ChipGap * _s;
        rows = labels.Count > 0 ? 1 : 0;
        for (int i = 0; i < labels.Count && i < MaxEffects; i++)
        {
            float cw = MathF.Min(Width(g, labels[i], _fStrong), width - 2 * ChipPad * _s) + 2 * ChipPad * _s;
            if (cx > x && cx + cw > x + width) { cx = x; cy += h + gap; rows++; }
            rects.Add(new RectangleF(cx, cy, cw, h));
            cx += cw + gap;
        }
        return rects;
    }

    /// <summary>The device's settings as labelled rows: effect, colour, brightness and speed, and start-up.</summary>
    float Inspector(Graphics g, RectangleF area, LightDevice d, Lighting lights)
    {
        var s = d.State;
        float p = 18 * _s, labelW = 104 * _s, cx = area.X + p + labelW, right = area.Right - p, cw = right - cx;

        var chips = ChipLayout(g, d.Modes, cx, 0, cw, out int chipRows);
        float effectH = MathF.Max(60 * _s, 32 * _s + chipRows * (ChipH + ChipGap) * _s - ChipGap * _s);
        float colourH = 138 * _s, brightH = 60 * _s, startH = 60 * _s;
        var plate = new RectangleF(area.X, area.Y, area.Width, effectH + colourH + brightH + startH);
        Plate(g, plate);
        float y = plate.Y;

        // Effect: the device's own, as wrapping chips
        Text(g, "Effect", _fBody, Muted, plate.X + p, y + 34.5f * _s);
        float chipTop = y + 16 * _s;
        for (int i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            chip.Offset(0, chipTop);
            Hits.Add((chip, HitEffect + i));
            bool on = s.Brightness > 0 && i == s.Mode;
            FillRound(g, chip, 7 * _s, on ? Raised : Well, White(on ? 30 : 14));
            TextClipped(g, d.Modes[i], _fStrong, on || Hover(chip) ? Ink : Muted, chip.X + ChipPad * _s, chip.Y + chip.Height / 2 + 4.5f * _s, chip.Width - 2 * ChipPad * _s + 2);
        }
        y += effectH;
        Hairline(g, plate.X + p, right, y, 10);

        // Colour
        Text(g, "Colour", _fBody, Muted, plate.X + p, y + 34.5f * _s);
        bool colours = d.ModeUsesColours(s.Mode);
        var current = PickerColour(d);
        ToHsv(current, out float currentHue, out float sat);
        if (!_lightHueSynced)
        {
            _lightHueSynced = true;
            if (sat > 0.02f) _lightHue = currentHue; // start the hue thumb on the saved colour
        }
        float size = 22 * _s, sy = y + 19 * _s, pitch = MathF.Min(38 * _s, (cw - size) / (LightPresets.Length - 1));
        for (int i = 0; i < LightPresets.Length; i++)
        {
            var dot = new RectangleF(cx + i * pitch, sy, size, size);
            var hit = RectangleF.Inflate(dot, 5 * _s, 5 * _s);
            Hits.Add((hit, HitLightPreset + i));
            g.FillEllipse(Brush(LightPresets[i]), dot);
            bool selected = colours && LightPresets[i].ToArgb() == current.ToArgb();
            if (selected || Hover(hit))
            {
                using var ring = new Pen(selected ? Ink : White(90), 1.5f * _s);
                g.DrawEllipse(ring, RectangleF.Inflate(dot, 3.5f * _s, 3.5f * _s));
            }
        }
        TextRight(g, Settings.Hex(current), _fNum, Faint, right, sy + size / 2 + 4.5f * _s);
        float stripW = MathF.Min(cw, pitch * (LightPresets.Length - 1) + size), stripH = 10 * _s, hy = sy + size + 20 * _s;
        LightHueRect = new RectangleF(cx, hy, stripW, stripH);
        Hits.Add((RectangleF.Inflate(LightHueRect, 9 * _s, 9 * _s), HitLightHue));
        Strip(g, LightHueRect, Spectrum(1, 1), _lightHue / 360f, FromHsv(_lightHue, 1, 1));
        float syy = hy + stripH + 18 * _s;
        LightSatRect = new RectangleF(cx, syy, stripW, stripH);
        Hits.Add((RectangleF.Inflate(LightSatRect, 9 * _s, 9 * _s), HitLightSat));
        var fade = new ColorBlend(2) { Positions = [0f, 1f], Colors = [Color.White, FromHsv(_lightHue, 1, 1)] };
        Strip(g, LightSatRect, fade, sat, current);
        const string match = "Use accent colour";
        float my = syy + stripH + 28 * _s;
        var matchHit = new RectangleF(cx - 4 * _s, my - 16 * _s, Width(g, match, _fLabel) + 24 * _s, 24 * _s);
        Hits.Add((matchHit, HitMatchAccent));
        float dotR = 4 * _s;
        g.FillEllipse(Brush(Accent), cx, my - 4.5f * _s - dotR, dotR * 2, dotR * 2);
        Text(g, match, _fLabel, Hover(matchHit) ? Ink : Muted, cx + 15 * _s, my);
        y += colourH;
        Hairline(g, plate.X + p, right, y, 10);

        // Brightness and speed
        Text(g, "Brightness", _fBody, Muted, plate.X + p, y + 34.5f * _s);
        float end = Segmented(g, cx, y + 13 * _s, ["Off", "Low", "High"], s.Brightness, HitBright, true);
        bool hasSpeed = s.Brightness > 0 && d.ModeHasSpeed(s.Mode);
        float x3 = end + 26 * _s;
        float afterLabel = Text(g, "Speed", _fBody, hasSpeed ? Muted : Faint, x3, y + 34.5f * _s) + 12 * _s;
        if (hasSpeed || s.Brightness == 0)
            Segmented(g, afterLabel, y + 13 * _s, ["1", "2", "3", "4"], hasSpeed ? s.Speed - 1 : -1, HitSpeed, hasSpeed);
        else // a greyed-out control here read as broken; say why there is nothing to set
            TextClipped(g, "Not used by this effect", _fLabel, Faint, afterLabel, y + 34 * _s, right - afterLabel);
        y += brightH;
        Hairline(g, plate.X + p, right, y, 10);

        SwitchRow(g, new RectangleF(plate.X, y, plate.Width, startH), "Apply when Gauge starts", "Sets this lighting each time Gauge opens, including at sign-in", lights.RestoreOnLaunch, HitRestore);
        return plate.Bottom;
    }

    /// <summary>A row of mutually exclusive options. Returns the x of its right edge.</summary>
    float Segmented(Graphics g, float x, float y, ReadOnlySpan<string> labels, int active, int hitBase, bool enabled)
    {
        float segH = 28 * _s, pad = 14 * _s, inset = 3 * _s, total = 0;
        Span<float> widths = stackalloc float[labels.Length];
        for (int i = 0; i < labels.Length; i++) total += widths[i] = Width(g, labels[i], _fStrong) + pad * 2;
        var well = new RectangleF(x, y, total + inset * 2, segH + inset * 2);
        FillRound(g, well, 9 * _s, Well, White(14));
        float sx = well.X + inset;
        for (int i = 0; i < labels.Length; i++)
        {
            var seg = new RectangleF(sx, well.Y + inset, widths[i], segH);
            if (enabled) Hits.Add((seg, hitBase + i));
            bool on = i == active;
            if (on) FillRound(g, seg, 6.5f * _s, Raised, White(22));
            Text(g, labels[i], _fStrong, !enabled ? Faint : on || Hover(seg) ? Ink : Muted, sx + pad, seg.Y + segH / 2 + 4.5f * _s);
            sx += widths[i];
        }
        return well.Right;
    }

    /// <summary>A rounded gradient strip with a thumb at <paramref name="position"/> (0..1).</summary>
    void Strip(Graphics g, RectangleF r, ColorBlend colours, float position, Color thumb)
    {
        using (var path = Round(r, r.Height / 2))
        using (var brush = new LinearGradientBrush(new PointF(r.X - 1, 0), new PointF(r.Right + 1, 0), Color.Black, Color.Black) { InterpolationColors = colours })
            g.FillPath(brush, path);
        float tx = r.X + Math.Clamp(position, 0, 1) * r.Width, ty = r.Y + r.Height / 2, outer = 8.5f * _s, inner = 5.5f * _s;
        g.FillEllipse(Brush(Color.FromArgb(70, 0, 0, 0)), tx - outer - 1 * _s, ty - outer, (outer + 1 * _s) * 2, (outer + 1 * _s) * 2);
        g.FillEllipse(Brush(Ink), tx - outer, ty - outer, outer * 2, outer * 2);
        using var fill = new SolidBrush(thumb);
        g.FillEllipse(fill, tx - inner, ty - inner, inner * 2, inner * 2);
    }
}
