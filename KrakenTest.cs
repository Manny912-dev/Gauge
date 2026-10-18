using System.Drawing.Imaging;
using System.Text;

namespace Gauge;

/// <summary>
/// Developer check for the Kraken screen without the hardware: the protocol runs against a simulated cooler
/// that enforces the device's rules, the memory manager and the picture pipeline are checked on their own, and
/// the renderers are checked against the reference values in the specs.
/// <c>Gauge.exe --kraken-test &lt;folder&gt;</c> writes report.txt, sample frames and test GIFs.
/// </summary>
internal static class KrakenTest
{
    /// <summary>A stand-in cooler: RAM buckets, one display, a transfer state machine and its own status chatter.</summary>
    sealed class FakeKraken(int capacitySlots = KrakenMemory.TotalSlots) : IKrakenLink
    {
        public readonly Dictionary<int, (int Start, int Slots, byte Type, bool Graphic)> Buckets = new();
        public string Display = "firmware"; // "firmware" or "bucket N"
        public int Brightness = 50, Transferring = -1, Frames, Gifs;
        public readonly List<string> Commands = new();
        public readonly List<string> Problems = new();
        /// <summary>Every refusal the cooler sent, as "command → code", apart from "did not exist" on a delete.</summary>
        public readonly List<string> Refusals = new();
        readonly Queue<byte[]> _out = new();
        int _chatter;
        // the transfer under way
        bool _expectHeader;
        byte _type;
        long _length, _received;

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
                    if (Buckets.TryGetValue(report[2], out var b))
                    {
                        r[15] = (byte)(report[2] + 1); r[16] = 1;
                        r[17] = (byte)b.Start; r[18] = (byte)(b.Start >> 8); r[19] = (byte)b.Slots; r[20] = (byte)(b.Slots >> 8); r[21] = b.Type;
                    }
                    _out.Enqueue(r);
                    break;
                }
                case (0x32, 0x01):
                {
                    int idx = report[2], start = report[4] | report[5] << 8, slots = report[6] | report[7] << 8;
                    byte result = 1;
                    if (report[3] == 0) result = 7;
                    else if (idx >= KrakenMemory.Buckets) result = 3;
                    else if (Buckets.ContainsKey(idx)) result = 4;
                    else if (start + slots > capacitySlots || slots == 0) result = 6;
                    else if (Buckets.Values.Any(o => start < o.Start + o.Slots && o.Start < start + slots)) result = 5;
                    else Buckets[idx] = (start, slots, 0, false);
                    Answer(0x33, 0x01, result, "32 01");
                    break;
                }
                case (0x32, 0x02):
                {
                    int idx = report[2];
                    byte result = Display == $"bucket {idx}" ? (byte)9 : Buckets.Remove(idx) ? (byte)1 : (byte)8;
                    Answer(0x33, 0x02, result, "32 02");
                    break;
                }
                case (0x36, 0x01):
                {
                    int idx = report[2];
                    byte result = !Buckets.ContainsKey(idx) ? (byte)8 : Display == $"bucket {idx}" ? (byte)9 : (byte)1;
                    if (result == 1) { Transferring = idx; _expectHeader = true; _received = 0; }
                    Answer(0x37, 0x01, result, "36 01");
                    break;
                }
                case (0x36, 0x02):
                {
                    byte result = Transferring < 0 || _expectHeader ? (byte)16 : (byte)1;
                    if (result == 1)
                    {
                        if (_received != _length) Problems.Add($"payload ended at {_received} of {_length} bytes");
                        var b = Buckets[Transferring];
                        if ((long)b.Slots * 1024 < _length) Problems.Add($"bucket {Transferring} of {b.Slots} slots got {_length} bytes");
                        Buckets[Transferring] = (b.Start, b.Slots, _type, true);
                        Transferring = -1;
                        if (_type == KrakenSession.AssetGif) Gifs++; else Frames++;
                    }
                    Answer(0x37, 0x02, result, "36 02");
                    break;
                }
                case (0x36, 0x03): Transferring = -1; _out.Enqueue(Reply(0x37, 0x03, 1)); break;
                case (0x38, 0x01):
                {
                    byte result = 1;
                    if (report[2] == 0x02) Display = "firmware";
                    else if (report[2] == 0x04 && Buckets.TryGetValue(report[3], out var b) && b.Graphic) Display = $"bucket {report[3]}";
                    else result = report[2] == 0x04 ? (byte)10 : (byte)17;
                    Answer(0x39, 0x01, result, "38 01");
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

        void Answer(byte cmd, byte sub, byte result, string what)
        {
            if (result != 1 && !(what == "32 02" && result == 8)) Refusals.Add($"{what} → {result}");
            _out.Enqueue(Reply(cmd, sub, result));
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
            if (_expectHeader)
            {
                if (data.Length != 20 || !data[..12].SequenceEqual(KrakenSession.Header(0)[..12])) { Problems.Add($"bad header {Convert.ToHexString(data)}"); return; }
                _type = data[12];
                _length = BitConverter.ToUInt32(data[16..20]);
                if (_type is not (KrakenSession.AssetGif or KrakenSession.AssetRaw)) Problems.Add($"asset type {_type}");
                if (_type == KrakenSession.AssetRaw && _length != KrakenSession.FrameBytes) Problems.Add($"RAW frame of {_length} bytes");
                _expectHeader = false;
                return;
            }
            _received += data.Length;
            if (_received > _length) Problems.Add($"payload of {_received} bytes for a header of {_length}");
        }

        public void Dispose() { }
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        int bad = 0;
        void Check(bool ok, string what) { if (!ok) bad++; sb.AppendLine($"{(ok ? "ok  " : "FAIL")}  {what}"); }
        void Section(string title) => sb.AppendLine($"\n-- {title}");
        var frame = new byte[KrakenSession.FrameBytes];

        Section("protocol: setup, ping-pong, status, hand-back");
        // 1. Setup next to a bucket another program left in RAM
        var kraken = new FakeKraken();
        kraken.Buckets[0] = (0, 100, KrakenSession.AssetRaw, true);
        var session = new KrakenSession(kraken);
        session.Init();
        Check(session.BrightnessOnDevice == 50, "reads the screen's brightness (50)");
        session.SetBrightness(80);
        Check(kraken.Brightness == 80, "brightness 80 sent with 30 02 01 50, no upload");
        Check(kraken.Commands[0] == "36 03", "starts by cancelling any half-finished transfer");
        Check(kraken.Commands.Count(c => c == "30 04") == 16, "reads all 16 buckets at start");

        // 2. Ping-pong uploads: never touch the bucket on screen
        for (int i = 0; i < 4; i++) session.Upload(frame);
        Check(kraken.Frames == 4 && kraken.Problems.Count == 0, $"four frames uploaded cleanly ({kraken.Frames}, problems: {string.Join("; ", kraken.Problems)})");
        Check(kraken.Display == "bucket 15", $"last frame on screen in bucket 15 (shows {kraken.Display})");
        Check(kraken.Buckets[14].Start == 100 && kraken.Buckets[15].Start == 500, $"our buckets start after the other program's ({kraken.Buckets[14].Start}, {kraken.Buckets[15].Start})");
        Check(kraken.Buckets.ContainsKey(0), "the other program's bucket is left alone while there is room");
        int setups = kraken.Commands.Count(c => c == "32 01");
        Check(setups == 2, $"each bucket is set up once, then reused ({setups} setups for 4 frames)");

        // 3. Status through the chatter
        var (liquid, pump) = session.ReadStatus();
        Check(MathF.Abs(liquid - 35.4f) < 0.01f && pump == 2100, $"liquid {liquid} °C and pump {pump} rpm read past unrelated reports");

        // 4. Handing the screen back
        session.ShowFirmwareScreen();
        Check(kraken.Display == "firmware", "gives the screen back to the Kraken's own display");
        session.Upload(frame);
        Check(kraken.Display.StartsWith("bucket") && kraken.Problems.Count == 0 && kraken.Refusals.Count == 0, $"and can take it over again (refusals: {string.Join("; ", kraken.Refusals)})");

        // 5. Little room: the other program's bucket goes only when ours need its slots
        var tight = new FakeKraken(capacitySlots: 1000);
        tight.Buckets[3] = (300, 300, KrakenSession.AssetRaw, true);
        var s2 = new KrakenSession(tight);
        s2.Init();
        for (int i = 0; i < 3; i++) s2.Upload(frame);
        Check(tight.Frames == 3 && !tight.Buckets.ContainsKey(3) && tight.Buckets[14].Start == 600 && tight.Buckets[15].Start == 0 && tight.Problems.Count == 0,
            $"no room: the oldest other bucket was cleared to fit (ours at {tight.Buckets.GetValueOrDefault(14).Start} and {tight.Buckets.GetValueOrDefault(15).Start}; problems: {string.Join("; ", tight.Problems)})");

        Section("slot math and bulk header");
        Check(KrakenMemory.SlotsFor(409_600) == 400, "409,600 bytes = 400 slots");
        Check(KrakenMemory.SlotsFor(1) == 1 && KrakenMemory.SlotsFor(1024) == 1 && KrakenMemory.SlotsFor(1025) == 2, "slots = ceil(bytes / 1024): 1 → 1, 1024 → 1, 1025 → 2");
        Check(KrakenMemory.SlotsFor(24_903_680) == 24_320 && KrakenSession.MaxGifBytes == 24_903_680, "the largest GIF (24,903,680 bytes) fills all 24,320 slots");
        Check(Convert.ToHexString(KrakenSession.Header(409_600, KrakenSession.AssetRaw)) == "12FA01E8ABCDEF98765432100200000000400600", "RAW header: 12 FA 01 E8 AB CD EF 98 76 54 32 10 02 00 00 00 00 40 06 00");
        Check(Convert.ToHexString(KrakenSession.Header(1_234_567, KrakenSession.AssetGif)) == "12FA01E8ABCDEF987654321001000000" + "87D61200", "GIF header: type 01, length 1,234,567 little-endian (87 D6 12 00)");

        Section("memory manager");
        var mem = new KrakenMemory();
        Check(mem.FindRange(400, -1) == 0, "empty memory: first range at slot 0");
        mem.Set(1, new(0, 1000, 2));
        mem.Set(2, new(2000, 500, 2));
        Check(mem.FindRange(1000, -1) == 1000 && mem.FindRange(1001, -1) == 2500, "finds the first gap that fits (1000 fits between, 1001 goes after)");
        Check(mem.FindRange(1001, 2) == 1000, "the bucket being replaced does not count as taken");
        Check(mem.FindRange(KrakenMemory.TotalSlots, -1) == -1 && mem.FindRange(KrakenMemory.TotalSlots, 1) == -1, "no range when the whole memory is needed and something is in the way");
        mem.Found(5, new(5000, 100, 1)); // left by another program: oldest of all
        mem.Touch(1);
        Check(mem.Victim(-1, []) == 5, "evicts the least recently used first (a bucket found on the cooler)");
        mem.Shown = 5;
        Check(mem.Victim(-1, []) == 2, "never evicts the bucket on screen");
        Check(mem.Victim(-1, [2]) == 1, "kept buckets wait until nothing else is left");
        Check(mem.Victim(2, [1]) == 1, "…and then go too; the bucket being replaced is never a victim");
        mem.Clear(1); mem.Clear(2);
        Check(mem.Victim(-1, []) == -1, "nothing to evict when only the shown bucket is left");
        mem.Clear(5); mem.Shown = -1;
        mem.Set(0, new(0, 100, 2)); mem.Set(1, new(150, 100, 2));
        Check(mem.FindRange(50, -1) == 100 && mem.FindRange(51, -1) == 250, "ranges never overlap a bucket (50 fits the hole at 100, 51 does not)");

        Section("switching between live frames and GIFs");
        var dev = new FakeKraken();
        var s3 = new KrakenSession(dev);
        s3.Init();
        s3.Upload(frame); s3.Upload(frame);
        var smallGif = new byte[100_000];
        Check(s3.ShowGif(smallGif, "small") && dev.Display == "bucket 13" && dev.Gifs == 1, "a GIF goes into bucket 13 once and is shown");
        float lastProgress = 0;
        s3.Upload(frame);
        Check(dev.Display.StartsWith("bucket 1") && dev.Display != "bucket 13" && dev.Buckets.ContainsKey(13), "back to live frames; the GIF stays on the cooler while there is room");
        Check(!s3.ShowGif(smallGif, "small") && dev.Gifs == 1 && dev.Display == "bucket 13", "the same GIF again is not re-sent");
        var bigGif = new byte[24_000 * 1024]; // nearly all of the memory
        Check(s3.ShowGif(bigGif, "big", p => lastProgress = p) && dev.Display == "bucket 13" && dev.Buckets[13].Slots == 24_000, "a near-maximum GIF gets the room it needs");
        Check(!dev.Buckets.ContainsKey(14) && !dev.Buckets.ContainsKey(15), "…by clearing the live-frame buckets");
        Check(MathF.Abs(lastProgress - 1) < 0.001f, $"upload progress reaches 100 % ({lastProgress:P0})");
        s3.Upload(frame);
        Check(dev.Display is "bucket 14" or "bucket 15" && !dev.Buckets.ContainsKey(13), $"back to Dual/Single after it: the GIF bucket makes way and live frames return (shows {dev.Display})");
        Check(s3.ShowGif(bigGif, "big") && dev.Display == "bucket 13", "and the big GIF can come back");
        s3.Upload(frame);
        Check(dev.Refusals.Count == 0 && dev.Problems.Count == 0, $"GIF → live → GIF → live never hit result 5 or 9 (refusals: {string.Join("; ", dev.Refusals)}; problems: {string.Join("; ", dev.Problems)})");

        // Another program changed the memory behind Gauge's back: result 5 makes it read the buckets again
        var stale = new FakeKraken();
        var s4 = new KrakenSession(stale);
        s4.Init();
        stale.Buckets[2] = (0, 2000, KrakenSession.AssetGif, true);
        s4.Upload(frame);
        Check(stale.Refusals.Count == 1 && stale.Refusals[0] == "32 01 → 5" && stale.Display.StartsWith("bucket") && stale.Buckets[RawAOf(stale)].Start >= 2000,
            $"an overlap (result 5) is recovered by reading the buckets again ({string.Join("; ", stale.Refusals)})");

        // A GIF left from the last run is recognised and not sent again
        var again = new KrakenSession(dev);
        again.Init(s3.GifPlacement);
        s3.ShowGif(bigGif, "big"); // make sure it is on the cooler
        string? placement = s3.GifPlacement;
        var fresh = new KrakenSession(dev);
        fresh.Init(placement);
        int gifsBefore = dev.Gifs;
        Check(!fresh.ShowGif(bigGif, "big") && dev.Gifs == gifsBefore, $"after a restart the GIF on the cooler is reused ({placement})");

        Section("only RAM and screen commands");
        var allowed = new HashSet<string> { "30 01", "30 02", "30 04", "32 01", "32 02", "36 01", "36 02", "36 03", "38 01", "74 01" };
        var used = kraken.Commands.Concat(tight.Commands).Concat(dev.Commands).Concat(stale.Commands).Distinct().ToList();
        Check(used.All(allowed.Contains), $"commands used: {string.Join(", ", used)}");

        Section("rotation dial");
        bool formulas = true;
        for (int a = 0; a < 360; a += 30)
        {
            int stored = ((90 - a) % 360 + 360) % 360; // CAM's stored value R
            formulas &= KrakenFrame.RawClockwise(a) == (stored + 270) % 360 && KrakenFrame.GifClockwise(a) == stored
                && KrakenFrame.GifClockwise(a) == (KrakenFrame.RawClockwise(a) + 90) % 360 && KrakenFrame.RawClockwise(a) == (360 - a) % 360;
        }
        Check(formulas, "all 12 positions: RAW turns CW (R + 270) = CCW by a; GIF turns CW R = RAW + 90 (R = 90 − a)");
        Check(KrakenFrame.RawClockwise(0) == 0 && KrakenFrame.GifClockwise(0) == 90, "default dial: live frames untouched, GIF turned 90° clockwise");

        var dot = new uint[320 * 320];
        for (int y = 55; y < 66; y++) for (int x = 155; x < 166; x++) dot[y * 320 + x] = 0xFFFFFFFF; // a white square at the top, radius 100
        var turned = new uint[dot.Length];
        KrakenFrame.Rotate(dot, turned, 320, 30);
        var (bx, by) = Brightest(turned, 320);
        Check(MathF.Abs(bx - 210) <= 2 && MathF.Abs(by - 73.4f) <= 2, $"a 30° clockwise turn (bilinear) moves the top point to about (210, 73) (got {bx:0}, {by:0})");
        KrakenFrame.Rotate(dot, turned, 320, 90);
        (bx, by) = Brightest(turned, 320);
        Check(MathF.Abs(bx - 259.5f) <= 1 && MathF.Abs(by - 160) <= 1, $"a 90° turn is exact: top → right ({bx:0}, {by:0})");

        Section("single infographic (unchanged apart from rotation)");
        var settings = new LcdSettings();
        using (var img = KrakenFrame.Render(settings, 35))
        {
            img.Save(Path.Combine(dir, "ring-liquid-35.png"), ImageFormat.Png);
            float r = KrakenFrame.Normalise(LcdReading.LiquidTemp, 35);
            float theta = (225 - 270 * r) * MathF.PI / 180, rm = 0.95f * (1 - 0.075f * 0.95f) * 160;
            var px = img.GetPixel((int)(160 + rm * MathF.Cos(theta)), (int)(160 - rm * MathF.Sin(theta)));
            Check(Near(px, 0x70, 0x09, 0xDC, 8), $"dot at 10:30 is about #7009DC (got {Hex(px)})");
            var top = img.GetPixel(160, (int)(160 - rm));
            Check(Near(top, 0x91, 0x1C, 0xD3, 8), $"top of the ring is about #911CD3 (got {Hex(top)})");
            Check(img.GetPixel(160, 160 - 150).R > 100 && img.GetPixel(160, 160 - 155).ToArgb() == Color.Black.ToArgb(), "ring outer edge at radius 152 of 160");
            Check(img.GetPixel(160, (int)(160 + rm)).ToArgb() == Color.Black.ToArgb(), "90° opening at the bottom");
            var rgba = KrakenFrame.ToRgba(img, 0);
            bool identical = true;
            for (int y = 0; y < 320 && identical; y += 7)
                for (int x = 0; x < 320; x += 7)
                {
                    var c = img.GetPixel(x, y);
                    int o = (y * 320 + x) * 4;
                    identical &= rgba[o] == c.R && rgba[o + 1] == c.G && rgba[o + 2] == c.B && rgba[o + 3] == 255;
                }
            Check(rgba.Length == 409600 && identical, "dial at 0: the uploaded frame is the rendered frame, untouched, 409,600 bytes of RGBA");
            var c0 = img.GetPixel(20, 300);
            var cw90 = KrakenFrame.ToRgba(img, 270); // the dial at 270 turns the picture back 90° clockwise
            int at = (20 * 320 + 19) * 4; // a 90° clockwise turn puts (20, 300) at (19, 20)
            Check(cw90[at] == c0.R && cw90[at + 1] == c0.G && cw90[at + 2] == c0.B, "dial at 270: the frame is turned 90° clockwise");
            SaveRgba(KrakenFrame.ToRgba(img, 30), Path.Combine(dir, "ring-liquid-35-dial-30-uploaded.png"));
            SaveRgba(KrakenFrame.ToRgba(img, 90), Path.Combine(dir, "ring-liquid-35-dial-90-uploaded.png"));
        }

        Section("dual infographic");
        Check(KrakenFrame.DualSpan(0) == 0 && MathF.Abs(KrakenFrame.DualSpan(0.46f) - 33.12f) < 0.01f && KrakenFrame.DualSpan(1) == 72, "arc half-sweep r·72°: 0 → 0°, 0.46 → 33.1°, 1 → 72°");
        foreach (float r in (ReadOnlySpan<float>)[0, 0.46f, 1])
        {
            var (ca, cb, rad) = KrakenFrame.DualCaps(180, r);
            float ra = MathF.Sqrt(ca.X * ca.X + ca.Y * ca.Y), angA = MathF.Atan2(ca.Y, ca.X) * 180 / MathF.PI, angB = MathF.Atan2(cb.Y, cb.X) * 180 / MathF.PI;
            float reach = MathF.Asin(rad / 0.894f) * 180 / MathF.PI, apart = MathF.Abs(((angA - angB) % 360 + 540) % 360 - 180);
            Check(MathF.Abs(ra - 0.894f) < 1e-4 && rad == 0.094f && MathF.Abs(apart - 2 * KrakenFrame.DualSpan(r)) < 0.01f,
                $"r = {r}: caps of radius 0.094 centred at radius 0.894, {2 * KrakenFrame.DualSpan(r):0.0}° apart; with the caps the arc reaches 180 ± {KrakenFrame.DualSpan(r) + reach:0.0}°");
        }
        Check(MathF.Abs(KrakenFrame.DualSpan(0.46f) + MathF.Asin(0.094f / 0.894f) * 180 / MathF.PI - 39.2f) < 0.2f, "spec check 1: CPU 48 °C reaches about 141°–219° with its caps");
        var track = KrakenFrame.DualTrack(Color.Black);
        Check(track.R == 21 && track.G == 21 && track.B == 21, $"track colour on black is (21,21,21) (got {track.R},{track.G},{track.B})");
        var dual = new LcdSettings { Mode = LcdMode.Dual };
        using (var img = KrakenFrame.RenderDual(dual, 48, 60))
        {
            img.Save(Path.Combine(dir, "dual-cpu-48-gpu-60.png"), ImageFormat.Png);
            Color At(float deg, float radius) => img.GetPixel((int)MathF.Round(160 + radius * 160 * MathF.Cos(deg * MathF.PI / 180) - 0.5f), (int)MathF.Round(160 - radius * 160 * MathF.Sin(deg * MathF.PI / 180) - 0.5f));
            Check(Near(At(180, 0.894f), 0x6B, 0x00, 0xDE, 3) && Near(At(150, 0.894f), 0x6B, 0x00, 0xDE, 3), $"reading 1 arc in #6B00DE at 9 o'clock and up to 30° above it (got {Hex(At(180, 0.894f))})");
            Check(Near(At(0, 0.894f), 0xD6, 0x00, 0xBF, 3) && Near(At(-35, 0.894f), 0xD6, 0x00, 0xBF, 3), $"reading 2 arc in #D600BF at 3 o'clock and 35° below it (got {Hex(At(0, 0.894f))})");
            Check(Near(At(225, 0.894f), 21, 21, 21, 2) && Near(At(90, 0.894f), 21, 21, 21, 2), $"track ring where no arc reaches (225° and the top: {Hex(At(225, 0.894f))})");
            Check(At(180, 0.79f).R < 30 && At(180, 0.81f).R > 80 && At(180, 0.99f).ToArgb() == Color.Black.ToArgb() || At(180, 0.995f).R < 20, "arc band from radius 0.80 to 0.988 (about 128–158 px)");
            Check(img.GetPixel(104, 160).R > 200 || img.GetPixel(100, 160).R > 200 || img.GetPixel(110, 160).R > 200, "reading 1's number sits on the column at x = 104");
        }
        using (var img = KrakenFrame.RenderDual(dual, 0, 0))
        {
            img.Save(Path.Combine(dir, "dual-zero.png"), ImageFormat.Png);
            Color At(float deg) => img.GetPixel((int)MathF.Round(160 + 0.894f * 160 * MathF.Cos(deg * MathF.PI / 180) - 0.5f), (int)MathF.Round(160 - 0.894f * 160 * MathF.Sin(deg * MathF.PI / 180) - 0.5f));
            Check(Near(At(180), 0x6B, 0x00, 0xDE, 3) && Near(At(0), 0xD6, 0x00, 0xBF, 3) && Near(At(170), 21, 21, 21, 2) && Near(At(10), 21, 21, 21, 2), "spec check 2: r = 0 leaves only two dots at 9 and 3 o'clock on the track");
        }
        using (var img = KrakenFrame.RenderDual(new LcdSettings { Mode = LcdMode.Dual, Reading1 = LcdReading.CpuClock, Reading2 = LcdReading.GpuLoad }, 5432, 100))
        {
            img.Save(Path.Combine(dir, "dual-clock-and-load-100.png"), ImageFormat.Png);
            Color At(float deg) => img.GetPixel((int)MathF.Round(160 + 0.894f * 160 * MathF.Cos(deg * MathF.PI / 180) - 0.5f), (int)MathF.Round(160 - 0.894f * 160 * MathF.Sin(deg * MathF.PI / 180) - 0.5f));
            Check(Near(At(70), 0xD6, 0x00, 0xBF, 3) && Near(At(90), 21, 21, 21, 2), "r = 1 reaches ±72° (plus caps) and stops short of the top");
            bool clear = true;
            for (int y = 130; y < 180; y++) for (int x = 0; x < 320; x++)
                {
                    float dx = x + 0.5f - 160, dy = y + 0.5f - 160;
                    if (MathF.Sqrt(dx * dx + dy * dy) is > 120 and < 127 && img.GetPixel(x, y).R > 200 && img.GetPixel(x, y).G > 200) clear = false;
                }
            Check(clear, "a four-digit clock shrinks to stay clear of the arcs");
        }

        Section("pictures");
        string src = Path.Combine(dir, "white-400x300.png");
        using (var white = new Bitmap(400, 300))
        {
            using (var g = Graphics.FromImage(white)) g.Clear(Color.White);
            white.Save(src, ImageFormat.Png);
        }
        var gif = KrakenMedia.ProcessForTest(src, new Rectangle(50, 0, 300, 300), 0);
        var frames = KrakenMedia.DecodeForTest(gif, out int w, out int h, out int repeat);
        Check(w == 320 && h == 320 && frames.Count == 1, $"a PNG becomes a 320×320 one-frame GIF ({w}×{h}, {frames.Count} frame(s))");
        uint Pixel(uint[] p, float dx, float dy) => p[(int)(160 + dy - 0.5f) * 320 + (int)(160 + dx - 0.5f)];
        static bool White(uint p) => (p & 0xFF) > 200 && (p >> 8 & 0xFF) > 200 && (p >> 16 & 0xFF) > 200;
        static bool Black(uint p) => (p & 0xFFFFFF) == 0;
        var f0 = frames[0].Pixels;
        float d165 = 165 / MathF.Sqrt(2), d169 = 169 / MathF.Sqrt(2); // the mask only reaches the corners: radius 167 is past the screen's edges
        Check(White(Pixel(f0, d165, d165)) && Black(Pixel(f0, d169, d169)) && White(Pixel(f0, -d165, -d165)) && Black(Pixel(f0, -d169, -d169)) && Black(f0[0]) && Black(f0[^1]) && White(Pixel(f0, 159, 0)),
            "corners masked: white inside radius 167 (checked on the diagonals at 165), pure black beyond it (169)");

        string arrow = Path.Combine(dir, "arrow-test.gif");
        KrakenMedia.WriteArrowGif(arrow);
        foreach (int dial in (ReadOnlySpan<int>)[0, 30, 90])
        {
            var out1 = KrakenMedia.ProcessForTest(arrow, new Rectangle(0, 0, 320, 320), KrakenFrame.GifClockwise(dial));
            File.WriteAllBytes(Path.Combine(dir, $"arrow-dial-{dial}-as-uploaded.gif"), out1);
            var af = KrakenMedia.DecodeForTest(out1, out _, out _, out int loops);
            if (dial == 0)
            {
                Check(af.Count == 8 && af.All(f => f.Delay == 10) && loops == 0, $"GIF frames and delays kept, looping forever ({af.Count} frames, delays {string.Join(",", af.Select(f => f.Delay).Distinct())}, repeat {loops})");
                // turned 90° clockwise: the arrow's tip (top) now points right
                Check(White(Pixel(af[0].Pixels, 100, 0)) && !White(Pixel(af[0].Pixels, 0, -100)), "at the default dial the uploaded GIF is turned 90° clockwise (arrow points right in the file)");
            }
        }

        string noise = Path.Combine(dir, "noise.gif");
        KrakenMedia.WriteNoiseGif(noise, frames: 12, size: 320);
        var full = KrakenMedia.ProcessForTest(noise, new Rectangle(0, 0, 320, 320), 0, limit: int.MaxValue);
        int limit = full.Length / 3;
        var shrunk = KrakenMedia.ProcessForTest(noise, new Rectangle(0, 0, 320, 320), 0, limit);
        var sf = KrakenMedia.DecodeForTest(shrunk, out _, out _, out _);
        Check(shrunk.Length <= limit && sf.Count < 12 && sf.Sum(f => f.Delay) == 12 * 4, $"too big: shrinks to fit ({full.Length:N0} → {shrunk.Length:N0} bytes, {sf.Count} frames, same total time {sf.Sum(f => f.Delay)})");
        bool refused = false;
        try { KrakenMedia.ProcessForTest(noise, new Rectangle(0, 0, 320, 320), 0, limit: 2000); }
        catch (KrakenMediaException ex) { refused = ex.Message.Contains("23.75"); }
        Check(refused, "a picture that cannot be made small enough is refused with the 23.75 MB message");
        Check(KrakenMedia.DefaultCrop(new Size(400, 300)) == new Rectangle(57, 7, 285, 285), $"default crop: 95 % of the short side, centred ({KrakenMedia.DefaultCrop(new Size(400, 300))})");

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
        using (var img = KrakenFrame.RenderDual(new LcdSettings { Mode = LcdMode.Dual, Logo = false, Reading1 = LcdReading.LiquidTemp, Number1 = Color.FromArgb(255, 0x6B, 0x00, 0xDE), Label2 = Color.FromArgb(255, 0x7D, 0x84, 0x8F) }, float.NaN, 100))
            img.Save(Path.Combine(dir, "dual-no-reading-colours.png"), ImageFormat.Png);

        sb.Insert(0, $"Kraken LCD self-test: {(bad == 0 ? "all passed" : $"{bad} failed")}\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString());
        return bad;
    }

    static int RawAOf(FakeKraken k) => k.Display == "bucket 15" ? 15 : 14;

    static (float X, float Y) Brightest(uint[] p, int size)
    {
        float sx = 0, sy = 0, total = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float v = p[y * size + x] & 0xFF;
                if (v < 128) continue;
                sx += (x + 0.5f) * v; sy += (y + 0.5f) * v; total += v;
            }
        return total == 0 ? (float.NaN, float.NaN) : (sx / total, sy / total);
    }

    static void SaveRgba(byte[] rgba, string path)
    {
        using var bmp = new Bitmap(320, 320, PixelFormat.Format32bppArgb);
        for (int y = 0; y < 320; y++)
            for (int x = 0; x < 320; x++)
            {
                int o = (y * 320 + x) * 4;
                bmp.SetPixel(x, y, Color.FromArgb(255, rgba[o], rgba[o + 1], rgba[o + 2]));
            }
        bmp.Save(path, ImageFormat.Png);
    }

    static bool Near(Color c, int r, int g, int b, int tolerance) => Math.Abs(c.R - r) <= tolerance && Math.Abs(c.G - g) <= tolerance && Math.Abs(c.B - b) <= tolerance;
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
