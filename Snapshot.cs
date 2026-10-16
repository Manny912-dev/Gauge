using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace Gauge;

/// <summary>Developer helpers: render the UI to PNG without a window, and generate the app icon.</summary>
internal static class Snapshot
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        using var t = new Telemetry();
        t.Start();
        Thread.Sleep(3000);
        var self = Process.GetCurrentProcess();
        double cpu0 = self.TotalProcessorTime.TotalMilliseconds;
        Thread.Sleep(9000);
        self.Refresh();
        double sampleMs = (self.TotalProcessorTime.TotalMilliseconds - cpu0) / 9;

        const float s = 1.25f;
        int w = (int)(1060 * s), h = (int)(680 * s);
        using var r = new Renderer(t, s);
        Save(r, t, w, h, Path.Combine(dir, "monitor.png"));
        r.Tab = Renderer.PageSettings;
        r.StartWithWindows = true;
        r.SetAccent(Renderer.Presets[4]);
        Save(r, t, w, h, Path.Combine(dir, "settings.png"));
        r.SetAccent(Renderer.DefaultAccent);
        r.StartWithWindows = false;
        // The smallest window the app allows
        Save(r, t, (int)(880 * s), (int)(580 * s), Path.Combine(dir, "settings-small.png"));
        r.Specs = Specs.Collect(t, out var summary);
        r.Summary = summary;
        r.Tab = 1;
        Save(r, t, w, h, Path.Combine(dir, "specs.png"));
        r.Scroll = 400 * s;
        Save(r, t, w, h, Path.Combine(dir, "specs-scrolled.png"));
        r.Scroll = 0;
        var lights = new Lighting();
        lights.Scan(); // read-only
        r.Lights = lights;
        r.Tab = 2;
        Save(r, t, w, h, Path.Combine(dir, "lighting.png"));
        File.WriteAllText(Path.Combine(dir, "lighting-report.txt"), lights.DeviceReport());
        // Sample entries, so the "no driver yet" list can be checked on a PC that has none.
        lights.Unclaimed.Add(new UnclaimedDevice("Corsair", "CORSAIR iCUE H150i ELITE CAPELLIX", 0x1B1C, 0x0C1C, "FF42:0001 in65 out65 feat0"));
        lights.Unclaimed.Add(new UnclaimedDevice("NZXT", "NZXT RGB & Fan Controller", 0x1E71, 0x2009, "FF72:00A1 in64 out64 feat0"));
        lights.Unclaimed.Add(new UnclaimedDevice("Logitech", "G502 HERO Gaming Mouse", 0x046D, 0xC08B, "FF00:0001 in7 out7 feat0"));
        Save(r, t, w, h, Path.Combine(dir, "lighting-unclaimed.png"));
        // A desktop's worth of devices, so the list can be checked on a PC without them.
        lock (lights.Sync)
        {
            lights.Devices.Add(new SampleLight("Graphics card", "MSI GeForce RTX 4080 16GB GAMING X TRIO", ["GPU", "Logo"], ["Direct", "Static", "Breathing", "Rainbow", "Spectrum cycle"], [Color.FromArgb(255, 0, 208, 255), Color.FromArgb(255, 42, 91, 255)]));
            lights.Devices.Add(new SampleLight("Motherboard", "ASUS ROG STRIX Z790-H GAMING WIFI", ["Aura Mainboard", "Header 1", "Addressable 1", "Addressable 2"], ["Direct", "Static", "Breathing", "Flashing", "Spectrum cycle", "Rainbow", "Chase fade", "Chase", "Random flicker", "Music"], [Color.FromArgb(255, 224, 0, 255), Color.FromArgb(255, 160, 0, 255), Color.FromArgb(255, 89, 0, 255), Color.FromArgb(255, 65, 0, 255)]) { Mode = 4 });
            lights.Devices.Add(new SampleLight("Keyboard", "Razer Huntsman Elite", ["Keyboard", "Underglow"], ["Direct", "Static", "Breathing", "Spectrum cycle", "Wave"], [Color.FromArgb(255, 255, 122, 0), Color.FromArgb(255, 255, 212, 0)]));
            lights.Devices.Add(new SampleLight("Mouse", "Razer Basilisk V3 Pro", ["Logo", "Scroll wheel", "Underglow"], ["Direct", "Static", "Breathing", "Spectrum cycle"], [Color.White, Color.White, Color.White]) { Brightness = 0 });
        }
        Save(r, t, w, h, Path.Combine(dir, "lighting-desktop.png"));
        r.LightingPress(Renderer.HitLightDevice + 2, 0); // the motherboard: many effects, a self-colouring one chosen
        Save(r, t, w, h, Path.Combine(dir, "lighting-desktop-board.png"));
        Save(r, t, (int)(880 * s), (int)(580 * s), Path.Combine(dir, "lighting-small.png"));
        r.Scroll = 1000;
        Save(r, t, (int)(880 * s), (int)(580 * s), Path.Combine(dir, "lighting-small-scrolled.png"));
        r.Scroll = 0;
        r.LightingPress(Renderer.HitLightDevice, 0);
        lock (lights.Sync) lights.Devices.RemoveAll(d => d is SampleLight);
        lights.Unclaimed.Clear();
        r.Tab = 0;
        Save(r, t, (int)(880 * s), (int)(580 * s), Path.Combine(dir, "monitor-small.png"));

        var sb = new StringBuilder();
        r.Tab = 0;
        var sw = Stopwatch.StartNew();
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(bmp))
            for (int i = 0; i < 20; i++) lock (t.Sync) r.Render(g, w, h);
        sb.AppendLine($"sampling cpu per tick = {sampleMs:0.0} ms, render = {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
        r.Tab = 1;
        lock (t.Sync)
        {
            sb.AppendLine($"elevated={t.Elevated} netPerProcess={t.NetPerProcess}");
            sb.AppendLine($"cpu load={t.CpuLoad:0.0} temp={t.CpuTemp:0.0} clock={t.CpuClock:0} fan={t.CpuFan} power={t.CpuPower}");
            foreach (var g in t.Gpus) sb.AppendLine($"gpu {g.Name}: load={g.Load} temp={g.Temp} clock={g.Clock} fan={g.Fan} power={g.Power} vram={g.VramUsed}/{g.VramTotal}");
            sb.AppendLine($"net down={t.NetDown:0} up={t.NetUp:0} adapter={t.NetName}");
            float down = 0, up = 0;
            foreach (var p in t.Processes.Rows) { down += p.Down; up += p.Up; }
            sb.AppendLine($"per-process net total down={down:0} up={up:0}");
            foreach (var p in t.Processes.Rows) if (p.Down + p.Up > 0) sb.AppendLine($"  net {p.Name} down={p.Down:0} up={p.Up:0}");
            for (int i = 0; i < 8 && i < t.Processes.Rows.Count; i++)
            {
                var p = t.Processes.Rows[i];
                sb.AppendLine($"  {p.Name} x{p.Count} cpu={p.Cpu:0.0} gpu={p.Gpu:0.0} mem={p.Mem >> 20}MB down={p.Down:0} up={p.Up:0}");
            }
        }
        sb.AppendLine(Specs.ToText(r.Specs));
        File.WriteAllText(Path.Combine(dir, "info.txt"), sb.ToString());
    }

    /// <summary>Developer check of the OpenRGB path: scan, then send and read back every device.</summary>
    public static void OpenRgbTest(string resultPath)
    {
        var sb = new StringBuilder();
        var clock = Stopwatch.StartNew();
        using var lights = new Lighting();
        lights.Scan();
        sb.AppendLine($"scan took {clock.Elapsed.TotalSeconds:0.0} s; engine problem: {lights.EngineProblem ?? "none"}");
        lock (lights.Sync) foreach (var d in lights.Devices) sb.AppendLine($"  {d.Kind}: {d.Name} | {d.Detail} | zones {d.ZoneNames.Length} | modes {d.Modes.Length}: {string.Join(", ", d.Modes)}");
        sb.AppendLine(lights.RoundTripTest());
        var sample = new OrgbMode { Name = "Breathing", Value = 3, Flags = OrgbMode.HasSpeed | OrgbMode.HasBrightness | OrgbMode.HasModeColour,
            SpeedMin = 100, SpeedMax = 10, BrightnessMin = 0, BrightnessMax = 255, ColoursMin = 1, ColoursMax = 2,
            Speed = 40, Brightness = 128, Direction = 1, ColourMode = OrgbMode.ColoursModeSpecific, Colours = [0x0000FF, 0x00FF00] };
        sb.AppendLine($"mode packet round-trips: {OrgbClient.ModeRoundTrips(sample)}");
        var engines = Process.GetProcessesByName("OpenRGB");
        sb.AppendLine($"engine processes while connected: {engines.Length}");
        foreach (var e in engines) e.Dispose();
        File.WriteAllText(resultPath, sb.ToString());
    }

    /// <summary>Opens the tray menu with its first item hovered and saves a picture of it from the screen.</summary>
    public static void TrayMenu(string path)
    {
        int dpi = (int)Native.GetDpiForSystem();
        using var tray = new Tray(dpi);
        var menu = tray.Menu;
        menu.Show(new Point(240, 240));
        menu.Items[2].Select();
        for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(20); }
        var area = Rectangle.Inflate(menu.Bounds, 24, 24);
        using var bmp = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bmp.Save(path, ImageFormat.Png);
        menu.Close();
    }

    /// <summary>Developer check of a lighting driver: sends one static colour and records what happened.</summary>
    public static void LightTest(string hex, string resultPath)
    {
        var sb = new StringBuilder();
        try
        {
            var lights = new Lighting();
            lights.Scan();
            sb.AppendLine($"devices={lights.Devices.Count} rival={lights.Rival ?? "none"}");
            if (lights.Devices.Count > 0 && Settings.ParseColor(hex) is Color c)
            {
                var d = lights.Devices[0];
                var state = new LightState { Zones = [c, c, c, c], Brightness = 2, Mode = 0, Speed = 2 };
                sb.AppendLine($"device={d.Name} [{d.Detail}]");
                sb.AppendLine("report=" + Convert.ToHexString(Lenovo4ZoneKeyboard.BuildReport(state)));
                d.Send(state);
                sb.AppendLine("sent=ok");
            }
        }
        catch (Exception ex) { sb.AppendLine("error=" + ex); }
        File.WriteAllText(resultPath, sb.ToString());
    }

    static void Save(Renderer r, Telemetry t, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
            lock (t.Sync) r.Render(g, w, h);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>The image the installer shows while it works, drawn to match the app.</summary>
    public static void WriteSplash(string path)
    {
        const int w = 520, h = 280;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var shade = new LinearGradientBrush(new Point(0, 0), new Point(0, h), Color.FromArgb(0x17, 0x19, 0x1D), Color.FromArgb(0x0D, 0x0E, 0x11)))
                g.FillRectangle(shade, 0, 0, w, h);
            using (var edge = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
                g.DrawRectangle(edge, 0, 0, w - 1, h - 1);

            using var brand = new Font("Bahnschrift SemiBold", 44, FontStyle.Regular, GraphicsUnit.Pixel);
            using var label = new Font("Bahnschrift SemiBold", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0xF2, 0xF3, 0xF5));
            using var faint = new SolidBrush(Color.FromArgb(0x7A, 0x80, 0x8A));
            var fmt = StringFormat.GenericTypographic;
            float textW = g.MeasureString("Gauge", brand, PointF.Empty, fmt).Width, mark = 22, gap = 22;
            float left = (w - (mark * 2 + gap + textW)) / 2, cy = h / 2f - 14;

            using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var value = new Pen(Color.FromArgb(0xFF, 0xA5, 0x3C), 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(track, left, cy - mark + 2, mark * 2, mark * 2, 135, 270);
                g.DrawArc(value, left, cy - mark + 2, mark * 2, mark * 2, 135, 170);
            }
            g.DrawString("Gauge", brand, ink, left + mark * 2 + gap, cy - 27, fmt);

            // Tracked capitals, as the app sets its labels
            const string caption = "SYSTEM MONITOR";
            float cw = 0;
            foreach (char ch in caption) cw += g.MeasureString(ch.ToString(), label, PointF.Empty, fmt).Width + 2.4f;
            float x = (w - cw) / 2;
            foreach (char ch in caption)
            {
                g.DrawString(ch.ToString(), label, faint, x, cy + 48, fmt);
                x += g.MeasureString(ch.ToString(), label, PointF.Empty, fmt).Width + 2.4f;
            }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// The README banner: brand mark, wordmark and tagline over a live-looking load chart in the
    /// app's own style. Drawn at twice its display size so it stays sharp on high-DPI screens.
    /// </summary>
    public static void WriteBanner(string path)
    {
        const float k = 2, lw = 1280, lh = 300; // logical size; the image is k times larger
        int w = (int)(lw * k), h = (int)(lh * k);
        var amber = Color.FromArgb(0xFF, 0xA5, 0x3C);
        float radius = 22 * k;

        static GraphicsPath Rounded(RectangleF r, float d)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The chart goes on its own layer first, so it can fade out under the wordmark.
        using var chart = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(chart))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int n = 64;
            float baseY = h + 2, top = 104 * k, left = 600 * k, right = 1176 * k, step = (right - left) / (n - 1);
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                // A calm, deterministic "load" trace that builds toward the right edge
                float t = i / (float)(n - 1);
                float v = 0.26f + 0.30f * t * t + 0.09f * MathF.Sin(i * 0.19f + 0.6f) + 0.035f * MathF.Sin(i * 0.47f + 1.9f);
                pts[i] = new PointF(left + i * step, baseY - Math.Clamp(v, 0.05f, 1) * (baseY - top));
            }
            using (var area = new GraphicsPath())
            {
                area.AddCurve(pts, 0.35f);
                area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, baseY);
                area.AddLine(pts[^1].X, baseY, left, baseY);
                area.CloseFigure();
                using var fill = new LinearGradientBrush(new PointF(0, top - 10 * k), new PointF(0, baseY), Color.FromArgb(64, amber), Color.FromArgb(0, amber));
                g.FillPath(fill, area);
            }
            using (var line = new Pen(amber, 3.2f * k) { LineJoin = LineJoin.Round })
                g.DrawCurve(line, pts, 0.35f);
            var end = pts[^1];
            foreach (var (r, a) in (ReadOnlySpan<(float, int)>)[(22 * k, 26), (13 * k, 50)])
                using (var halo = new SolidBrush(Color.FromArgb(a, amber))) g.FillEllipse(halo, end.X - r, end.Y - r, r * 2, r * 2);
            using (var dot = new SolidBrush(amber)) g.FillEllipse(dot, end.X - 6 * k, end.Y - 6 * k, 12 * k, 12 * k);
        }
        // Fade the chart in from the left: invisible under the text, full strength from the middle on.
        var data = chart.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        unsafe
        {
            float from = 640 * k, to = 900 * k;
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    float f = Math.Clamp((x - from) / (to - from), 0, 1);
                    f = f * f * (3 - 2 * f);
                    row[x * 4 + 3] = (byte)(row[x * 4 + 3] * f);
                }
            }
        }
        chart.UnlockBits(data);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var card = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using var shape = Rounded(card, radius * 2);
            using (var shade = new LinearGradientBrush(new PointF(0, 0), new PointF(0, h), Color.FromArgb(0x17, 0x18, 0x1D), Color.FromArgb(0x0B, 0x0C, 0x0E)))
                g.FillPath(shade, shape);
            g.SetClip(shape);

            // A warm glow where the trace ends, and the app's faint dotted guide lines
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(760 * k, 40 * k, 700 * k, 420 * k);
                using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(40, amber), SurroundColors = [Color.FromArgb(0, amber)] };
                g.FillPath(glow, glowPath);
            }
            using (var dots = new Pen(Color.FromArgb(22, 255, 255, 255), 1.2f * k) { DashPattern = [1f, 3f] })
                foreach (float y in (ReadOnlySpan<float>)[104, 202])
                    g.DrawLine(dots, 700 * k, y * k, w, y * k);
            g.DrawImage(chart, 0, 0);
            g.ResetClip();
            using (var edge = new Pen(Color.FromArgb(26, 255, 255, 255), 1.4f * k))
                g.DrawPath(edge, shape);

            // Brand: the gauge mark, the wordmark and one line about what it is
            float mx = 64 * k, cy = 122 * k, mr = 33 * k;
            using (var track = new Pen(Color.FromArgb(64, 255, 255, 255), 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var value = new Pen(amber, 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(track, mx, cy - mr + 3 * k, mr * 2, mr * 2, 135, 270);
                g.DrawArc(value, mx, cy - mr + 3 * k, mr * 2, mr * 2, 135, 170);
            }
            var fmt = StringFormat.GenericTypographic;
            using var word = new Font("Bahnschrift SemiBold", 86 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            using var tag = new Font("Segoe UI", 24 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0xF2, 0xF3, 0xF5));
            using var muted = new SolidBrush(Color.FromArgb(0x9A, 0xA0, 0xA9));
            float ascent = word.Size * word.FontFamily.GetCellAscent(FontStyle.Regular) / word.FontFamily.GetEmHeight(FontStyle.Regular);
            float wordX = mx + mr * 2 + 20 * k, baseline = cy + 31 * k;
            g.DrawString("Gauge", word, ink, wordX, baseline - ascent, fmt);
            float tagAscent = tag.Size * tag.FontFamily.GetCellAscent(FontStyle.Regular) / tag.FontFamily.GetEmHeight(FontStyle.Regular);
            g.DrawString("System monitor and RGB lighting for Windows", tag, muted, mx + 2 * k, baseline + 62 * k - tagAscent, fmt);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    public static void WriteIcon(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float pad = size * 0.04f, d = size * 0.44f;
                using var tile = new GraphicsPath();
                var r = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                tile.AddArc(r.X, r.Y, d, d, 180, 90);
                tile.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                tile.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                tile.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                tile.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(0x16, 0x18, 0x1B));
                g.FillPath(fill, tile);

                float stroke = MathF.Max(1.6f, size * 0.105f), inset = size * 0.25f;
                var arc = new RectangleF(inset, inset + size * 0.03f, size - 2 * inset, size - 2 * inset);
                using var track = new Pen(Color.FromArgb(0x4A, 0x50, 0x59), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var value = new Pen(Color.FromArgb(0xF0, 0xA6, 0x3C), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(track, arc, 135, 270);
                g.DrawArc(value, arc, 135, 170);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            images.Add(ms.ToArray());
        }

        using var file = new BinaryWriter(File.Create(path));
        file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            file.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            file.Write((byte)0); file.Write((byte)0);
            file.Write((ushort)1); file.Write((ushort)32);
            file.Write(images[i].Length); file.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) file.Write(img);
    }
}

/// <summary>A pretend lighting device for snapshots: sends nothing.</summary>
internal sealed class SampleLight : LightDevice
{
    public SampleLight(string kind, string name, string[] zones, string[] modes, Color[] colours)
    {
        Kind = kind;
        Name = name;
        Detail = "Sample device";
        ZoneNames = zones;
        Modes = modes;
        State.Zones = colours;
        State.Mode = 1;
    }

    public int Mode { set => State.Mode = value; }
    public int Brightness { set => State.Brightness = value; }
    public override string Key => "sample." + Name;
    public override bool ModeUsesColours(int mode) => mode is 0 or 1 or 2;
    public override bool ModeHasSpeed(int mode) => mode >= 2;
    public override int ColourMode => 1;
    public override void Send(LightState state) { }
}