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
    /// The README banner: brand mark, wordmark and two lines about Gauge, centred over a load trace
    /// in the app's chart style that runs the full width and dims where it passes behind the text.
    /// Drawn at three times its display size (3840 x 1080) so it stays sharp on 4K screens.
    /// </summary>
    public static void WriteBanner(string path)
    {
        const float k = 3, lw = 1280, lh = 360; // logical size; the image is k times larger
        int w = (int)(lw * k), h = (int)(lh * k);
        var amber = Color.FromArgb(0xFF, 0xA5, 0x3C);
        float radius = 22 * k, centreX = w / 2f;
        const string leadText = "A lightweight system monitor and RGB lighting controller for Windows.";
        using var lead = new Font("Segoe UI", 25 * k, FontStyle.Regular, GraphicsUnit.Pixel);
        float leadW;
        using (var probe = new Bitmap(1, 1)) using (var pg = Graphics.FromImage(probe)) leadW = pg.MeasureString(leadText, lead, PointF.Empty, StringFormat.GenericTypographic).Width;
        // The text block the trace passes behind, and how softly the dimming fades out around it
        var block = RectangleF.FromLTRB(centreX - leadW / 2 - 28 * k, 66 * k, centreX + leadW / 2 + 28 * k, 252 * k);
        float feather = 70 * k;

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

        // The trace, its guide lines and its glow go on their own layer, so they can be dimmed behind the text.
        using var chart = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var fillLayer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        float endX;
        using (var g = Graphics.FromImage(chart))
        using (var gf = Graphics.FromImage(fillLayer))
        {
            gf.SmoothingMode = SmoothingMode.AntiAlias;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const int n = 72;
            float baseY = h + 2, top = 96 * k, left = -8 * k, right = 1240 * k, step = (right - left) / (n - 1);
            using (var dots = new Pen(Color.FromArgb(24, 255, 255, 255), 1.2f * k) { DashPattern = [1f, 3f] })
                foreach (float y in (ReadOnlySpan<float>)[96, 228])
                    g.DrawLine(dots, 0, y * k, w, y * k);

            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                // A calm, deterministic "load" trace: higher at the sides, low under the text, rising to the end.
                float t = i / (float)(n - 1);
                float v = 0.46f + 0.12f * MathF.Sin(3 * MathF.PI * t - 0.4f) + 0.10f * t * t + 0.03f * MathF.Sin(i * 0.43f + 0.8f) + 0.015f * MathF.Sin(i * 0.97f + 2.1f);
                pts[i] = new PointF(left + i * step, baseY - Math.Clamp(v, 0.05f, 1) * (baseY - top));
            }
            using (var area = new GraphicsPath())
            {
                area.AddCurve(pts, 0.35f);
                area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, baseY);
                area.AddLine(pts[^1].X, baseY, left, baseY);
                area.CloseFigure();
                using var fill = new LinearGradientBrush(new PointF(0, top + 40 * k), new PointF(0, baseY), Color.FromArgb(60, amber), Color.FromArgb(0, amber));
                gf.FillPath(fill, area);
            }
            using (var line = new Pen(amber, 3.2f * k) { LineJoin = LineJoin.Round })
                g.DrawCurve(line, pts, 0.35f);
            var end = pts[^1];
            endX = end.X;
            foreach (var (r, a) in (ReadOnlySpan<(float, int)>)[(22 * k, 26), (13 * k, 50)])
                using (var halo = new SolidBrush(Color.FromArgb(a, amber))) g.FillEllipse(halo, end.X - r, end.Y - r, r * 2, r * 2);
            using (var dot = new SolidBrush(amber)) g.FillEllipse(dot, end.X - 6 * k, end.Y - 6 * k, 12 * k, 12 * k);
        }
        // Dim both layers behind the text block (feathered, so the trace slips smoothly behind the words),
        // ease them in from the left edge, and let the fill thin out toward the end of the trace.
        static float Smooth(float e) { e = Math.Clamp(e, 0, 1); return e * e * (3 - 2 * e); }
        foreach (var (layer, isFill) in new[] { (chart, false), (fillLayer, true) })
        {
            var data = layer.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    float oy = MathF.Max(0, MathF.Max(block.Top - y, y - block.Bottom));
                    for (int x = 0; x < w; x++)
                    {
                        if (row[x * 4 + 3] == 0) continue;
                        float ox = MathF.Max(0, MathF.Max(block.Left - x, x - block.Right));
                        float d = MathF.Sqrt(ox * ox + oy * oy) / feather;
                        float behind = MathF.Exp(-d * d * 2.2f); // 1 inside the block, gone a feather's width outside
                        float f = (1 - 0.70f * behind) * Smooth(x / (90 * k));
                        if (isFill) f *= 1 - 0.75f * Smooth((x - (endX - 260 * k)) / (260 * k));
                        row[x * 4 + 3] = (byte)(row[x * 4 + 3] * f);
                    }
                }
            }
            layer.UnlockBits(data);
        }

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
            // A warm glow where the trace ends
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(820 * k, 60 * k, 640 * k, 440 * k);
                using var glow = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(36, amber), SurroundColors = [Color.FromArgb(0, amber)] };
                g.FillPath(glow, glowPath);
            }
            g.DrawImage(fillLayer, 0, 0);
            g.DrawImage(chart, 0, 0);
            g.ResetClip();
            using (var edge = new Pen(Color.FromArgb(26, 255, 255, 255), 1.4f * k))
                g.DrawPath(edge, shape);

            // Brand: the gauge mark and wordmark, centred together, then two lines about Gauge
            var fmt = StringFormat.GenericTypographic;
            using var word = new Font("Bahnschrift SemiBold", 84 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            using var sub = new Font("Segoe UI", 21 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0xF2, 0xF3, 0xF5));
            using var soft = new SolidBrush(Color.FromArgb(0xA8, 0xAE, 0xB7));
            static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(FontStyle.Regular) / f.FontFamily.GetEmHeight(FontStyle.Regular);

            float mr = 32 * k, gap = 22 * k, wordW = g.MeasureString("Gauge", word, PointF.Empty, fmt).Width;
            float groupX = centreX - (mr * 2 + gap + wordW) / 2, baseline = 138 * k, cy = baseline - 31 * k;
            using (var track = new Pen(Color.FromArgb(64, 255, 255, 255), 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var value = new Pen(amber, 9 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(track, groupX, cy - mr + 3 * k, mr * 2, mr * 2, 135, 270);
                g.DrawArc(value, groupX, cy - mr + 3 * k, mr * 2, mr * 2, 135, 170);
            }
            g.DrawString("Gauge", word, ink, groupX + mr * 2 + gap, baseline - Ascent(word), fmt);

            void Centred(string s, Font f, Brush b, float base_)
            {
                float tw = g.MeasureString(s, f, PointF.Empty, fmt).Width;
                g.DrawString(s, f, b, centreX - tw / 2, base_ - Ascent(f), fmt);
            }
            Centred(leadText, lead, ink, 200 * k);
            Centred("No account, no telemetry, no vendor apps. About 40 MB of memory.", sub, soft, 238 * k);
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