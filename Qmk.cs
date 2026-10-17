using System.Text;
using HidSharp;

namespace Gauge;

/// <summary>
/// A QMK keyboard whose lighting is set through VIA: QMK's raw HID interface (usage page 0xFF60,
/// usage 0x61) carrying VIA's custom-value commands for the RGB matrix. VIA numbers effects by the
/// firmware's own effect list, which depends on how each board was built, so only boards whose list
/// is known are driven. Changes are sent without saving and saved to the keyboard once they settle.
/// </summary>
internal sealed class QmkViaKeyboard : LightDevice
{
    const uint RawHidUsage = 0xFF600061;
    const int ReportLength = 33; // report id 0 plus VIA's 32-byte message
    const byte GetProtocolVersion = 0x01, SetValue = 0x07, GetValue = 0x08, SaveValues = 0x09, Unhandled = 0xFF;
    const byte RgbMatrixChannel = 3, Brightness = 1, Effect = 2, EffectSpeed = 3, Colour = 4;
    /// <summary>VIA protocol 12 ("v3") added channel-addressed custom values.</summary>
    const int MinProtocol = 12;

    readonly record struct Fx(string Name, byte Mode, bool UsesColour);
    sealed record Board(int Vendor, int Product, string Name, Fx[] Effects);

    // Mode numbers follow QMK's effect order with only the board's enabled effects counted
    // (0 is "off", 1 is always solid colour). Q1 HE: the animations in Keychron's
    // keyboards/keychron/q1_he/info.json, ordered as in quantum/rgb_matrix/animations/rgb_matrix_effects.inc.
    static readonly Board[] s_boards =
    [
        new(0x3434, 0x0B10, "Keychron Q1 HE",
        [
            new("Solid colour", 1, true), new("Breathing", 2, true), new("Band spiral", 3, true),
            new("Cycle all", 4, false), new("Cycle left to right", 5, false), new("Cycle up and down", 6, false),
            new("Rainbow chevron", 7, false), new("Cycle out to in", 8, false), new("Cycle out to in, dual", 9, false),
            new("Cycle pinwheel", 10, false), new("Cycle spiral", 11, false), new("Dual beacon", 12, true),
            new("Rainbow beacon", 13, false), new("Jellybean raindrops", 14, false), new("Pixel rain", 15, false),
            new("Typing heatmap", 16, false), new("Digital rain", 17, false), new("Reactive", 18, true),
            new("Reactive wide", 19, true), new("Reactive nexus", 20, true), new("Splash", 21, false),
            new("Solid splash", 22, true),
        ]),
    ];

    static readonly byte[] s_speeds = [40, 100, 170, 240]; // Gauge's speeds 1..4 on QMK's 0..255
    const byte LowBrightness = 110, HighBrightness = 255;

    readonly HidDevice? _hid; // null only in the self-test
    readonly byte[] _modes;
    readonly bool[] _colourModes;
    bool _unsaved;

    QmkViaKeyboard(HidDevice? hid, Board board, int protocol, byte[] values)
    {
        _hid = hid;
        VendorId = board.Vendor;
        ProductId = board.Product;
        Kind = "Keyboard";
        Name = board.Name;
        Detail = $"QMK  ·  VIA {protocol}  ·  {board.Vendor:X4}:{board.Product:X4}";
        ZoneNames = ["Keyboard"];

        // The keyboard may be running an effect this list does not name (a maker's own effect):
        // keep it selectable so opening Gauge never changes what the keyboard is doing.
        var fx = new List<Fx>(board.Effects);
        byte effect = values[1];
        if (effect != 0 && !fx.Exists(f => f.Mode == effect)) fx.Add(new($"Effect {effect}", effect, false));
        Modes = fx.ConvertAll(f => f.Name).ToArray();
        _modes = fx.ConvertAll(f => f.Mode).ToArray();
        _colourModes = fx.ConvertAll(f => f.UsesColour).ToArray();

        // Start from what the keyboard shows now.
        State.Mode = effect == 0 ? 0 : Math.Max(0, Array.IndexOf(_modes, effect));
        State.Brightness = effect == 0 || values[0] == 0 ? 0 : values[0] >= 160 ? 2 : 1;
        int nearest = 0;
        for (int i = 1; i < s_speeds.Length; i++)
            if (Math.Abs(s_speeds[i] - values[2]) < Math.Abs(s_speeds[nearest] - values[2])) nearest = i;
        State.Speed = nearest + 1;
        State.Zones = [Hsv(values[3], values[4])];
        Applied = true;
    }

    public override string Key => $"qmk.{VendorId:X4}{ProductId:X4}";
    public override bool ModeUsesColours(int mode) => mode >= 0 && mode < _colourModes.Length && _colourModes[mode];
    public override bool ModeHasSpeed(int mode) => mode >= 0 && mode < _modes.Length && _modes[mode] != 1;
    public override string Note => "Set through the keyboard's VIA interface, and saved on the keyboard a moment after you stop changing it.";

    // ------------------------------------------------------------------ finding keyboards

    /// <summary>Finds supported QMK boards that answer VIA's lighting queries. Asks, but changes nothing.</summary>
    public static List<LightDevice> Detect()
    {
        var found = new List<LightDevice>();
        foreach (var board in s_boards)
        {
            foreach (var hid in DeviceList.Local.GetHidDevices(board.Vendor, board.Product))
            {
                if (!IsRawHid(hid)) continue;
                try
                {
                    using var stream = Open(hid);
                    if (stream == null) continue;
                    if (Probe(m => Exchange(stream, m), out int protocol, out var values) != null) continue;
                    found.Add(new QmkViaKeyboard(hid, board, protocol, values));
                    break; // one per board
                }
                catch (Exception ex) { Log.Write($"qmk: {board.Name}: {ex.Message}"); }
            }
        }
        return found;
    }

    /// <summary>For the device report: what a QMK raw HID interface answers, or null if it is not one.</summary>
    public static string? Describe(HidDevice hid)
    {
        if (!IsRawHid(hid)) return null;
        try
        {
            using var stream = Open(hid);
            if (stream == null) return "QMK raw HID (busy)";
            string? problem = Probe(m => Exchange(stream, m), out int protocol, out var v);
            return problem != null ? $"QMK raw HID: {problem}" : $"VIA {protocol}, RGB matrix effect {v[1]}, brightness {v[0]}, speed {v[2]}, hue {v[3]}, sat {v[4]}";
        }
        catch (Exception ex) { return $"QMK raw HID: {ex.Message}"; }
    }

    static bool IsRawHid(HidDevice hid)
    {
        try
        {
            if (hid.GetMaxOutputReportLength() != ReportLength) return false;
            foreach (var item in hid.GetReportDescriptor().DeviceItems)
                foreach (uint u in item.Usages.GetAllValues())
                    if (u == RawHidUsage) return true;
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ talking to the keyboard

    static HidStream? Open(HidDevice hid)
    {
        if (!hid.TryOpen(out var stream)) return null;
        stream.ReadTimeout = 300;
        stream.WriteTimeout = 300;
        return stream;
    }

    /// <summary>Sends one VIA message over USB and returns the keyboard's 32-byte answer, or null if none came.</summary>
    static byte[]? Exchange(HidStream stream, byte[] message)
    {
        var report = new byte[ReportLength];
        Array.Copy(message, 0, report, 1, message.Length);
        stream.Write(report);
        var reply = new byte[ReportLength];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                int n = stream.Read(reply, 0, reply.Length);
                if (n < 4) continue;
                // QMK answers by echoing the message with the result filled in; anything else is stale.
                if (reply[1] == message[0] || reply[1] == Unhandled) return reply[1..];
            }
            catch (TimeoutException) { return null; }
        }
        return null;
    }

    /// <summary>Reads the protocol version and the RGB matrix values (brightness, effect, speed, hue, saturation). Null when usable.</summary>
    static string? Probe(Func<byte[], byte[]?> ask, out int protocol, out byte[] values)
    {
        values = new byte[5];
        protocol = 0;
        var reply = ask([GetProtocolVersion]);
        if (reply == null) return "no reply";
        if (reply[0] != GetProtocolVersion) return "not VIA";
        protocol = reply[1] << 8 | reply[2];
        if (protocol < MinProtocol) return $"VIA {protocol} is older than v3";
        foreach (var (id, index) in new[] { (Brightness, 0), (Effect, 1), (EffectSpeed, 2) })
        {
            var r = ask([GetValue, RgbMatrixChannel, id]);
            if (r == null || r[0] != GetValue || r[1] != RgbMatrixChannel || r[2] != id) return $"VIA {protocol}, no RGB matrix";
            values[index] = r[3];
        }
        var c = ask([GetValue, RgbMatrixChannel, Colour]);
        if (c == null || c[0] != GetValue || c[2] != Colour) return $"VIA {protocol}, no RGB matrix colour";
        values[3] = c[3];
        values[4] = c[4];
        return null;
    }

    static void Set(Func<byte[], byte[]?> ask, byte id, params byte[] data)
    {
        var message = new byte[3 + data.Length];
        message[0] = SetValue; message[1] = RgbMatrixChannel; message[2] = id;
        data.CopyTo(message, 3);
        var reply = ask(message) ?? throw new IOException("the keyboard did not answer");
        if (reply[0] == Unhandled) throw new IOException("the keyboard refused the lighting command");
    }

    public override void Send(LightState s)
    {
        using var stream = Open(_hid!) ?? throw new IOException("the keyboard is in use by another program");
        Apply(m => Exchange(stream, m), s);
    }

    /// <summary>The VIA messages for a state.</summary>
    void Apply(Func<byte[], byte[]?> ask, LightState s)
    {
        if (s.Brightness <= 0)
        {
            Set(ask, Effect, 0); // VIA's "off"
        }
        else
        {
            Set(ask, Effect, _modes[Math.Clamp(s.Mode, 0, _modes.Length - 1)]);
            Set(ask, Brightness, s.Brightness >= 2 ? HighBrightness : LowBrightness);
            Set(ask, EffectSpeed, s_speeds[Math.Clamp(s.Speed, 1, 4) - 1]);
            var (h, sat) = HueSat(s.Zones.Length > 0 ? s.Zones[0] : Color.White);
            Set(ask, Colour, h, sat);
        }
        _unsaved = true;
    }

    /// <summary>Writes the current lighting to the keyboard's own memory, once changes have stopped.</summary>
    public override void Settle()
    {
        if (!_unsaved) return;
        using var stream = Open(_hid!) ?? throw new IOException("the keyboard is in use by another program");
        Save(m => Exchange(stream, m));
    }

    void Save(Func<byte[], byte[]?> ask)
    {
        var reply = ask([SaveValues, RgbMatrixChannel]);
        if (reply == null || reply[0] == Unhandled) throw new IOException("the keyboard did not save its lighting");
        _unsaved = false;
    }

    static (byte Hue, byte Sat) HueSat(Color c)
    {
        float max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        int hue = (int)Math.Round(c.GetHue() / 360f * 256) & 0xFF; // QMK hue wraps at 256
        return ((byte)hue, (byte)(max == 0 ? 0 : Math.Round((max - min) / max * 255)));
    }

    static Color Hsv(byte hue, byte sat)
    {
        float h = hue / 256f * 360, s = sat / 255f, c = s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = 1 - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        return Color.FromArgb(255, (int)MathF.Round((r + m) * 255), (int)MathF.Round((g + m) * 255), (int)MathF.Round((b + m) * 255));
    }

    // ------------------------------------------------------------------ self-test

    /// <summary>
    /// A stand-in keyboard that follows VIA's rules: it echoes each message with the answer filled in,
    /// stores RGB matrix values, marks anything it does not handle with 0xFF, and records saves.
    /// </summary>
    sealed class FakeKeyboard(int protocol, bool rgbMatrix = true)
    {
        public byte Bright = 200, Effect = 4, Speed = 128, Hue = 21, Sat = 255;
        public int Saves;
        public readonly List<string> Log = new();

        public byte[]? Ask(byte[] message)
        {
            var r = new byte[32];
            message.CopyTo(r, 0);
            Log.Add(Convert.ToHexString(message));
            switch (message[0])
            {
                case GetProtocolVersion: r[1] = (byte)(protocol >> 8); r[2] = (byte)protocol; break;
                case GetValue when rgbMatrix && message[1] == RgbMatrixChannel:
                    switch (message[2])
                    {
                        case Brightness: r[3] = Bright; break;
                        case QmkViaKeyboard.Effect: r[3] = Effect; break;
                        case EffectSpeed: r[3] = Speed; break;
                        case Colour: r[3] = Hue; r[4] = Sat; break;
                        default: r[0] = Unhandled; break;
                    }
                    break;
                case SetValue when rgbMatrix && message[1] == RgbMatrixChannel:
                    switch (message[2])
                    {
                        case Brightness: Bright = message[3]; break;
                        case QmkViaKeyboard.Effect: Effect = message[3]; break;
                        case EffectSpeed: Speed = message[3]; break;
                        case Colour: Hue = message[3]; Sat = message[4]; break;
                        default: r[0] = Unhandled; break;
                    }
                    break;
                case SaveValues when rgbMatrix: Saves++; break;
                default: r[0] = Unhandled; break;
            }
            return r;
        }
    }

    /// <summary>Runs the driver against <see cref="FakeKeyboard"/>. Returns a report; <paramref name="failures"/> counts what went wrong.</summary>
    internal static string SelfTest(out int failures)
    {
        var sb = new StringBuilder();
        int bad = 0;
        void Check(bool ok, string what) { if (!ok) bad++; sb.AppendLine($"{(ok ? "ok  " : "FAIL")}  {what}"); }
        var board = s_boards[0];

        // 1. Reading the keyboard's current lighting
        var kb = new FakeKeyboard(0x000C);
        Check(Probe(kb.Ask, out int protocol, out var values) == null && protocol == 12, "probe accepts a VIA 12 keyboard with an RGB matrix");
        var d = new QmkViaKeyboard(null, board, protocol, values);
        Check(d.Modes[d.State.Mode] == "Cycle all", $"current effect 4 reads as 'Cycle all' (got '{d.Modes[d.State.Mode]}')");
        Check(d.State.Brightness == 2, "brightness 200 reads as High");
        Check(d.State.Speed == 2, $"speed 128 reads as 2 (got {d.State.Speed})");
        var orange = d.State.Zones[0];
        Check(orange.R == 255 && orange.G is > 100 and < 150 && orange.B == 0, $"hue 21 reads as orange (got {Settings.Hex(orange)})");
        Check(kb.Log.TrueForAll(m => m.StartsWith("01") || m.StartsWith("08")), "probing only asks: no set or save messages");

        // 2. Setting lighting
        var s = d.State.Clone();
        s.Mode = Array.IndexOf(d.Modes, "Solid colour"); s.Brightness = 2; s.Speed = 3; s.Zones = [Color.FromArgb(255, 255, 0, 0)];
        d.Apply(kb.Ask, s);
        Check(kb.Effect == 1 && kb.Bright == 255 && kb.Speed == 170 && kb.Hue == 0 && kb.Sat == 255, $"solid red, high, speed 3 -> effect {kb.Effect}, brightness {kb.Bright}, speed {kb.Speed}, hue {kb.Hue}, sat {kb.Sat}");
        s.Zones = [Color.FromArgb(255, 0, 0, 255)];
        d.Apply(kb.Ask, s);
        Check(kb.Hue == 171, $"blue -> hue 171 (got {kb.Hue})");
        s.Brightness = 1;
        s.Mode = Array.IndexOf(d.Modes, "Solid splash");
        d.Apply(kb.Ask, s);
        Check(kb.Effect == 22 && kb.Bright == LowBrightness, $"Solid splash, low -> effect {kb.Effect}, brightness {kb.Bright}");
        s.Brightness = 0;
        d.Apply(kb.Ask, s);
        Check(kb.Effect == 0, "Off -> effect 0");
        Check(kb.Saves == 0, "nothing saved while changing");
        d.Save(kb.Ask);
        Check(kb.Saves == 1, "saved once when settled");

        // 3. An effect the list does not name stays selectable and selected
        var custom = new FakeKeyboard(0x000C) { Effect = 23 };
        Probe(custom.Ask, out protocol, out values);
        var dc = new QmkViaKeyboard(null, board, protocol, values);
        Check(dc.Modes[dc.State.Mode] == "Effect 23", $"unknown effect 23 is kept as 'Effect 23' (got '{dc.Modes[dc.State.Mode]}')");
        var sc = dc.State.Clone();
        dc.Apply(custom.Ask, sc);
        Check(custom.Effect == 23, "re-sending it keeps effect 23");

        // 4. Keyboards Gauge must leave alone
        Check(Probe(new FakeKeyboard(0x0009).Ask, out _, out _) != null, "VIA 9 (older than v3) is declined");
        Check(Probe(new FakeKeyboard(0x000C, rgbMatrix: false).Ask, out _, out _) != null, "a keyboard without an RGB matrix is declined");
        Check(Probe(_ => null, out _, out _) == "no reply", "a silent interface is declined");

        // 5. Colour round trips through QMK's hue and saturation
        foreach (var c in new[] { Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 255, 0), Color.FromArgb(255, 0, 0, 255), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 0, 255) })
        {
            var (h, sat) = HueSat(c);
            var back = Hsv(h, sat);
            int err = Math.Max(Math.Abs(back.R - c.R), Math.Max(Math.Abs(back.G - c.G), Math.Abs(back.B - c.B)));
            Check(err <= 8, $"{Settings.Hex(c)} -> hue {h}, sat {sat} -> {Settings.Hex(back)}");
        }

        failures = bad;
        sb.Insert(0, $"QMK VIA driver self-test: {(bad == 0 ? "all passed" : $"{bad} failed")}\n");
        return sb.ToString();
    }
}
