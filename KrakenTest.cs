using System.Drawing.Imaging;
using System.Text;

namespace Gauge;

/// <summary>
/// Developer check for the Kraken screen without the hardware: the protocol runs against a simulated cooler
/// that enforces the device's rules, and the renderer is checked against the reference values in the spec.
/// <c>Gauge.exe --kraken-test &lt;folder&gt;</c> writes report.txt and sample frames.
/// </summary>
internal static class KrakenTest
{
    /// <summary>A stand-in cooler: RAM buckets, one display, a transfer state machine and its own status chatter.</summary>
    sealed class FakeKraken(int capacitySlots = 4096) : IKrakenLink
    {
        public readonly Dictionary<int, (int Start, int Slots, bool Graphic)> Buckets = new();
        public string Display = "firmware"; // "firmware" or "bucket N"
        public int Brightness = 50, Transferring = -1, Frames;
        public bool ExpectHeader;
        public readonly List<string> Commands = new();
        public readonly List<string> Problems = new();
        readonly Queue<byte[]> _out = new();
        int _chatter;

        public void Write(byte[] report)
        {
            if (report.Length != 64) Problems.Add($"report of {report.Length} bytes");
            byte cmd = report[0], sub = report[1];
            Commands.Add($"{cmd:X2} {sub:X2}");
            // The cooler talks on its own too: a status report before every other answer
            if (++_chatter % 2 == 0) _out.Enqueue(Reply(0x75, 0x02, 0));
            switch (cmd, sub)
            {
                case (0x30, 0x01): { var r = Reply(0x31, 0x01, 1); r[24] = (byte)Brightness; _out.Enqueue(r); break; }
                case (0x30, 0x02): if (report[2] == 1) Brightness = report[3]; break; // no answer
                case (0x30, 0x04):
                {
                    var r = Reply(0x31, 0x04, 1);
                    r[14] = report[2];
                    if (Buckets.TryGetValue(report[2], out var b)) { r[16] = 1; r[17] = (byte)b.Start; r[18] = (byte)(b.Start >> 8); r[19] = (byte)b.Slots; r[20] = (byte)(b.Slots >> 8); }
                    _out.Enqueue(r);
                    break;
                }
                case (0x32, 0x01):
                {
                    int idx = report[2], start = report[4] | report[5] << 8, slots = report[6] | report[7] << 8;
                    byte result = 1;
                    if (report[3] == 0) result = 7;
                    else if (Buckets.ContainsKey(idx)) result = 4;
                    else if (start + slots > capacitySlots || slots == 0) result = 6;
                    else if (Buckets.Values.Any(o => start < o.Start + o.Slots && o.Start < start + slots)) result = 5;
                    else Buckets[idx] = (start, slots, false);
                    _out.Enqueue(Reply(0x33, 0x01, result));
                    break;
                }
                case (0x32, 0x02):
                {
                    int idx = report[2];
                    byte result = Display == $"bucket {idx}" ? (byte)9 : Buckets.Remove(idx) ? (byte)1 : (byte)8;
                    _out.Enqueue(Reply(0x33, 0x02, result));
                    break;
                }
                case (0x36, 0x01):
                {
                    int idx = report[2];
                    byte result = !Buckets.ContainsKey(idx) ? (byte)8 : Display == $"bucket {idx}" ? (byte)9 : (byte)1;
                    if (result == 1) { Transferring = idx; ExpectHeader = true; }
                    _out.Enqueue(Reply(0x37, 0x01, result));
                    break;
                }
                case (0x36, 0x02):
                {
                    byte result = Transferring < 0 || ExpectHeader ? (byte)16 : (byte)1;
                    if (result == 1) { var b = Buckets[Transferring]; Buckets[Transferring] = (b.Start, b.Slots, true); Transferring = -1; Frames++; }
                    _out.Enqueue(Reply(0x37, 0x02, result));
                    break;
                }
                case (0x36, 0x03): Transferring = -1; _out.Enqueue(Reply(0x37, 0x03, 1)); break;
                case (0x38, 0x01):
                {
                    byte result = 1;
                    if (report[2] == 0x02) Display = "firmware";
                    else if (report[2] == 0x04 && Buckets.TryGetValue(report[3], out var b) && b.Graphic) Display = $"bucket {report[3]}";
                    else result = report[2] == 0x04 ? (byte)10 : (byte)17;
                    _out.Enqueue(Reply(0x39, 0x01, result));
                    break;
                }
                case (0x74, 0x01): { var r = Reply(0x75, 0x01, 0); r[15] = 35; r[16] = 4; r[17] = 0x34; r[18] = 0x08; _out.Enqueue(r); break; } // 35.4 °C, 2100 rpm
                default:
                {
                    var r = Reply(0xFF, 0x02, cmd);
                    Problems.Add($"unknown command {cmd:X2} {sub:X2}");
                    _out.Enqueue(r);
                    break;
                }
            }
        }

        static byte[] Reply(byte cmd, byte sub, byte result)
        {
            var r = new byte[64];
            r[0] = cmd; r[1] = sub; r[14] = result;
            return r;
        }

        public byte[]? Read(int timeoutMs) => _out.Count > 0 ? _out.Dequeue() : null;

        public void Bulk(ReadOnlySpan<byte> data)
        {
            if (Transferring < 0) { Problems.Add("bulk data outside a transfer"); return; }
            if (ExpectHeader)
            {
                var h = KrakenSession.Header(KrakenSession.FrameBytes);
                if (!data.SequenceEqual(h)) Problems.Add($"bad header {Convert.ToHexString(data)}");
                ExpectHeader = false;
            }
            else if (data.Length != KrakenSession.FrameBytes) Problems.Add($"payload of {data.Length} bytes");
        }

        public void Dispose() { }
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        int bad = 0;
        void Check(bool ok, string what) { if (!ok) bad++; sb.AppendLine($"{(ok ? "ok  " : "FAIL")}  {what}"); }
        var frame = new byte[KrakenSession.FrameBytes];

        // 1. Setup next to a bucket another program left in RAM
        var kraken = new FakeKraken();
        kraken.Buckets[0] = (0, 100, true);
        var session = new KrakenSession(kraken);
        session.Init();
        Check(session.BrightnessOnDevice == 50, "reads the screen's brightness (50)");
        session.SetBrightness(80);
        Check(kraken.Brightness == 80, "brightness 80 sent with 30 02 01 50, no upload");
        Check(kraken.Commands[0] == "36 03", "starts by cancelling any half-finished transfer");

        // 2. Ping-pong uploads: never touch the bucket on screen
        for (int i = 0; i < 4; i++) session.Upload(frame);
        Check(kraken.Frames == 4 && kraken.Problems.Count == 0, $"four frames uploaded cleanly ({kraken.Frames}, problems: {string.Join("; ", kraken.Problems)})");
        Check(kraken.Display == "bucket 15", $"last frame on screen in bucket 15 (shows {kraken.Display})");
        Check(kraken.Buckets[14].Start == 100 && kraken.Buckets[15].Start == 500, $"our buckets start after the other program's ({kraken.Buckets[14].Start}, {kraken.Buckets[15].Start})");
        Check(kraken.Buckets.ContainsKey(0), "the other program's bucket is left alone");
        int setups = kraken.Commands.Count(c => c == "32 01");
        Check(setups == 2, $"each bucket is set up once, then reused ({setups} setups for 4 frames)");

        // 3. Status through the chatter
        var (liquid, pump) = session.ReadStatus();
        Check(MathF.Abs(liquid - 35.4f) < 0.01f && pump == 2100, $"liquid {liquid} °C and pump {pump} rpm read past unrelated reports");

        // 4. Handing the screen back
        session.ShowFirmwareScreen();
        Check(kraken.Display == "firmware", "gives the screen back to the Kraken's own display");
        session.Upload(frame);
        Check(kraken.Display.StartsWith("bucket") && kraken.Problems.Count == 0, "and can take it over again");

        // 5. Not enough room after the other buckets: clears RAM buckets and starts at slot 0
        var tight = new FakeKraken(capacitySlots: 1000);
        tight.Buckets[3] = (300, 300, true);
        var s2 = new KrakenSession(tight);
        s2.Init();
        s2.Upload(frame);
        s2.Upload(frame);
        s2.Upload(frame); // the bucket cleared by the replan is set up again on its next turn
        Check(tight.Frames == 3 && tight.Buckets.ContainsKey(14) && tight.Buckets.ContainsKey(15) && tight.Buckets[14].Start == 0 && tight.Buckets[15].Start == 400, $"overlap or no room: replanned to slots 0 and 400 ({tight.Frames} frames, problems: {string.Join("; ", tight.Problems)})");

        // 6. Only screen and RAM commands are ever sent: no flash writes
        var allowed = new HashSet<string> { "30 01", "30 02", "30 04", "32 01", "32 02", "36 01", "36 02", "36 03", "38 01", "74 01" };
        var used = kraken.Commands.Concat(tight.Commands).Distinct().ToList();
        Check(used.All(allowed.Contains), $"commands used: {string.Join(", ", used)}");

        // 7. The image against the spec's reference values (defaults, liquid at 35 °C)
        var settings = new LcdSettings();
        using (var img = KrakenFrame.Render(settings, 35))
        {
            img.Save(Path.Combine(dir, "ring-liquid-35.png"), ImageFormat.Png);
            float r = KrakenFrame.Normalise(LcdReading.LiquidTemp, 35);
            float theta = (225 - 270 * r) * MathF.PI / 180, rm = 0.95f * (1 - 0.075f * 0.95f) * 160;
            var dot = img.GetPixel((int)(160 + rm * MathF.Cos(theta)), (int)(160 - rm * MathF.Sin(theta)));
            Check(Near(dot, 0x70, 0x09, 0xDC, 8), $"dot at 10:30 is about #7009DC (got {Hex(dot)})");
            var top = img.GetPixel(160, (int)(160 - rm));
            Check(Near(top, 0x91, 0x1C, 0xD3, 8), $"top of the ring is about #911CD3 (got {Hex(top)})");
            Check(img.GetPixel(160, 160 - 150).R > 100 && img.GetPixel(160, 160 - 155).ToArgb() == Color.Black.ToArgb(), "ring outer edge at radius 152 of 160");
            Check(img.GetPixel(160, (int)(160 + rm)).ToArgb() == Color.Black.ToArgb(), "90° opening at the bottom");
            var rgba = KrakenFrame.ToRgba(img, 0);
            Check(rgba.Length == 409600 && rgba[3] == 255, "frame is 409 600 bytes of RGBA");
            var px = img.GetPixel(20, 300);
            var turned = KrakenFrame.ToRgba(img, 90);
            int o = (20 * 320 + 19) * 4; // a 90° clockwise turn puts (20, 300) at (19, 20)
            Check(turned[o] == px.R && turned[o + 1] == px.G && turned[o + 2] == px.B, "rotation turns the image clockwise");
        }

        // Sample frames for a visual check
        void Sample(string name, Action<LcdSettings> set, float value)
        {
            var s = new LcdSettings();
            set(s);
            using var img = KrakenFrame.Render(s, value);
            img.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        }
        Sample("fill-gpu-load", s => { s.Fill = true; s.Reading = LcdReading.GpuLoad; }, 64);
        Sample("ring-cpu-load-100", s => s.Reading = LcdReading.CpuLoad, 100);
        Sample("ring-cpu-clock", s => { s.Reading = LcdReading.CpuClock; s.Color1 = Color.FromArgb(255, 0xFF, 0xA5, 0x3C); s.Color2 = Color.FromArgb(255, 0xFF, 0x4D, 0x4D); }, 4523);
        Sample("ring-no-reading", s => s.Logo = false, float.NaN);
        Sample("ring-liquid-0", _ => { }, 0);

        sb.Insert(0, $"Kraken LCD self-test: {(bad == 0 ? "all passed" : $"{bad} failed")}\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString());
        return bad;
    }

    static bool Near(Color c, int r, int g, int b, int tolerance) => Math.Abs(c.R - r) <= tolerance && Math.Abs(c.G - g) <= tolerance && Math.Abs(c.B - b) <= tolerance;
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
