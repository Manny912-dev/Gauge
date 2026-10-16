using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Gauge;

// OpenRGB integration. Gauge ships OpenRGB unmodified in its "engine" folder and runs it as a
// separate, hidden process, talking to it over its documented local network protocol (the SDK).
// No OpenRGB code is part of Gauge itself.

/// <summary>One effect a device offers, as the SDK describes it.</summary>
internal sealed class OrgbMode
{
    public const uint HasSpeed = 1 << 0, HasBrightness = 1 << 4, HasPerLedColour = 1 << 5, HasModeColour = 1 << 6;
    public const uint ColoursPerLed = 1, ColoursModeSpecific = 2;

    public string Name = "";
    public int Value;
    public uint Flags, SpeedMin, SpeedMax, BrightnessMin, BrightnessMax, ColoursMin, ColoursMax, Speed, Brightness, Direction, ColourMode;
    public uint[] Colours = [];

    public bool Has(uint flag) => (Flags & flag) != 0;
    public bool UsesColours => Has(HasPerLedColour) || Has(HasModeColour);
    public OrgbMode Clone() { var m = (OrgbMode)MemberwiseClone(); m.Colours = (uint[])Colours.Clone(); return m; }
}

internal sealed class OrgbZone
{
    public string Name = "";
    public int Type;
    public uint LedCount;
}

/// <summary>A device as OpenRGB reports it. <see cref="Index"/> is its position in OpenRGB's list.</summary>
internal sealed class OrgbController
{
    public int Index, Type, ActiveMode;
    public string Name = "", Vendor = "", Description = "", Version = "", Serial = "", Location = "";
    public OrgbMode[] Modes = [];
    public OrgbZone[] Zones = [];
    public uint[] Colours = [];

    static readonly Regex s_usbId = new(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The USB vendor and product id, when the location names a USB/HID path.</summary>
    public (int Vendor, int Product)? UsbId
    {
        get
        {
            var m = s_usbId.Match(Location);
            return m.Success ? (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)) : null;
        }
    }
}

/// <summary>
/// Client for the OpenRGB SDK, speaking protocol 3: the newest layout that every OpenRGB from
/// 0.6 on accepts, and enough for modes, brightness, speed and per-LED colour.
/// Not thread-safe; <see cref="Lighting"/> serialises calls.
/// </summary>
internal sealed class OrgbClient : IDisposable
{
    const int Protocol = 3;
    const uint ReqCount = 0, ReqData = 1, DeviceListUpdated = 100, ReqProtocol = 40, SetName = 50,
        UpdateLeds = 1050, UpdateModeId = 1101;

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;

    OrgbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _stream.ReadTimeout = 8000;
        _stream.WriteTimeout = 8000;
    }

    /// <summary>Connects to a local OpenRGB and agrees the protocol. Null when nothing answers.</summary>
    public static OrgbClient? TryConnect(int port, int timeoutMs)
    {
        var tcp = new TcpClient();
        try
        {
            if (!tcp.ConnectAsync(IPAddress.Loopback, port).Wait(timeoutMs)) { tcp.Dispose(); return null; }
            var client = new OrgbClient(tcp);
            var reply = client.Request(0, ReqProtocol, BitConverter.GetBytes((uint)Protocol), ReqProtocol);
            if (reply.Length < 4 || BitConverter.ToUInt32(reply, 0) < Protocol) { client.Dispose(); return null; }
            client.Send(0, SetName, Encoding.ASCII.GetBytes("Gauge\0"));
            return client;
        }
        catch { tcp.Dispose(); return null; }
    }

    void Send(uint device, uint id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[16];
        "ORGB"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header[4..], device);
        BitConverter.TryWriteBytes(header[8..], id);
        BitConverter.TryWriteBytes(header[12..], (uint)data.Length);
        _stream.Write(header);
        if (data.Length > 0) _stream.Write(data);
    }

    byte[] ReadExact(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(buffer, read, count - read);
            if (n <= 0) throw new IOException("OpenRGB closed the connection");
            read += n;
        }
        return buffer;
    }

    /// <summary>Sends a request and returns the reply with the given id, skipping unsolicited packets.</summary>
    byte[] Request(uint device, uint id, ReadOnlySpan<byte> data, uint replyId)
    {
        Send(device, id, data);
        for (int guard = 0; guard < 64; guard++)
        {
            var header = ReadExact(16);
            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B') throw new IOException("unexpected data from OpenRGB");
            uint gotId = BitConverter.ToUInt32(header, 8), size = BitConverter.ToUInt32(header, 12);
            if (size > 64 * 1024 * 1024) throw new IOException("oversized packet from OpenRGB");
            var body = size > 0 ? ReadExact((int)size) : [];
            if (gotId == replyId) return body;
            // DEVICE_LIST_UPDATED and anything else unasked for is ignored; the caller rescans.
        }
        throw new IOException("no reply from OpenRGB");
    }

    public int Count() => (int)BitConverter.ToUInt32(Request(0, ReqCount, [], ReqCount), 0);

    public OrgbController Controller(int index)
    {
        var data = Request((uint)index, ReqData, BitConverter.GetBytes((uint)Protocol), ReqData);
        using var r = new BinaryReader(new MemoryStream(data));
        string Str() { int n = r.ReadUInt16(); var b = r.ReadBytes(n); return Encoding.UTF8.GetString(b, 0, Math.Max(0, n - 1)); }

        var c = new OrgbController { Index = index };
        r.ReadUInt32(); // data size
        c.Type = r.ReadInt32();
        c.Name = Str(); c.Vendor = Str(); c.Description = Str(); c.Version = Str(); c.Serial = Str(); c.Location = Str();
        int modes = r.ReadUInt16();
        c.ActiveMode = r.ReadInt32();
        c.Modes = new OrgbMode[modes];
        for (int i = 0; i < modes; i++) c.Modes[i] = ReadMode(r, Str);
        int zones = r.ReadUInt16();
        c.Zones = new OrgbZone[zones];
        for (int i = 0; i < zones; i++)
        {
            var z = new OrgbZone { Name = Str(), Type = r.ReadInt32() };
            r.ReadUInt32(); r.ReadUInt32(); // leds min, max
            z.LedCount = r.ReadUInt32();
            int matrix = r.ReadUInt16();
            if (matrix > 0) r.ReadBytes(matrix);
            c.Zones[i] = z;
        }
        int leds = r.ReadUInt16();
        for (int i = 0; i < leds; i++) { Str(); r.ReadUInt32(); }
        int colours = r.ReadUInt16();
        c.Colours = new uint[colours];
        for (int i = 0; i < colours; i++) c.Colours[i] = r.ReadUInt32();
        return c;
    }

    static OrgbMode ReadMode(BinaryReader r, Func<string> str)
    {
        var m = new OrgbMode
        {
            Name = str(), Value = r.ReadInt32(), Flags = r.ReadUInt32(),
            SpeedMin = r.ReadUInt32(), SpeedMax = r.ReadUInt32(),
            BrightnessMin = r.ReadUInt32(), BrightnessMax = r.ReadUInt32(),
            ColoursMin = r.ReadUInt32(), ColoursMax = r.ReadUInt32(),
            Speed = r.ReadUInt32(), Brightness = r.ReadUInt32(), Direction = r.ReadUInt32(), ColourMode = r.ReadUInt32(),
        };
        int n = r.ReadUInt16();
        m.Colours = new uint[n];
        for (int i = 0; i < n; i++) m.Colours[i] = r.ReadUInt32();
        return m;
    }

    static void WriteMode(BinaryWriter w, OrgbMode m)
    {
        var name = Encoding.UTF8.GetBytes(m.Name + "\0");
        w.Write((ushort)name.Length); w.Write(name);
        w.Write(m.Value); w.Write(m.Flags);
        w.Write(m.SpeedMin); w.Write(m.SpeedMax);
        w.Write(m.BrightnessMin); w.Write(m.BrightnessMax);
        w.Write(m.ColoursMin); w.Write(m.ColoursMax);
        w.Write(m.Speed); w.Write(m.Brightness); w.Write(m.Direction); w.Write(m.ColourMode);
        w.Write((ushort)m.Colours.Length);
        foreach (uint c in m.Colours) w.Write(c);
    }

    /// <summary>Developer check: true when a mode survives being written and read back unchanged.</summary>
    internal static bool ModeRoundTrips(OrgbMode m)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) WriteMode(w, m);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        string Str() { int n = r.ReadUInt16(); var b = r.ReadBytes(n); return Encoding.UTF8.GetString(b, 0, Math.Max(0, n - 1)); }
        var back = ReadMode(r, Str);
        return ms.Position == ms.Length && back.Name == m.Name && back.Value == m.Value && back.Flags == m.Flags
            && back.SpeedMin == m.SpeedMin && back.SpeedMax == m.SpeedMax && back.BrightnessMin == m.BrightnessMin
            && back.BrightnessMax == m.BrightnessMax && back.ColoursMin == m.ColoursMin && back.ColoursMax == m.ColoursMax
            && back.Speed == m.Speed && back.Brightness == m.Brightness && back.Direction == m.Direction
            && back.ColourMode == m.ColourMode && back.Colours.AsSpan().SequenceEqual(m.Colours);
    }

    public void UpdateMode(int index, int modeIndex, OrgbMode mode)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0u); // data size, filled in below
            w.Write(modeIndex);
            WriteMode(w, mode);
        }
        var data = body.ToArray();
        BitConverter.TryWriteBytes(data, (uint)data.Length);
        Send((uint)index, UpdateModeId, data);
    }

    public void SetLeds(int index, uint[] colours)
    {
        var data = new byte[4 + 2 + colours.Length * 4];
        BitConverter.TryWriteBytes(data, (uint)data.Length);
        BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)colours.Length);
        for (int i = 0; i < colours.Length; i++) BitConverter.TryWriteBytes(data.AsSpan(6 + i * 4), colours[i]);
        Send((uint)index, UpdateLeds, data);
    }

    public static uint Pack(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));
    public static Color Unpack(uint v) => Color.FromArgb(255, (int)(v & 0xFF), (int)((v >> 8) & 0xFF), (int)((v >> 16) & 0xFF));

    public void Dispose() => _tcp.Dispose();
}

/// <summary>
/// Runs the bundled OpenRGB hidden: local-only server on a private port, its own settings folder,
/// tied to Gauge's lifetime by a Windows job object so it can never outlive Gauge. When the user
/// already runs their own OpenRGB, Gauge uses that instead of starting a second copy.
/// </summary>
internal sealed class OrgbEngine : IDisposable
{
    const int UserDefaultPort = 6742;

    // OpenRGB's detectors for the keyboards Gauge drives natively; two controllers on one device would fight.
    static readonly string[] s_nativeDetectors =
    [
        "Lenovo 5 2020", "Lenovo 5 2021", "Lenovo 5 2021 Ideapad", "Lenovo 5 2022", "Lenovo 5 2022 Ideapad",
        "Lenovo 5 2023", "Lenovo 5 2023 Ideapad", "Lenovo Ideapad 3-15ach6",
    ];

    Process? _process;
    nint _job;

    /// <summary>True when connected to the user's own OpenRGB rather than Gauge's.</summary>
    public bool UsingUsersCopy { get; private set; }
    /// <summary>True when connected to the protected engine that motherboard and RAM lighting set up.</summary>
    public bool UsingBoardEngine { get; private set; }
    /// <summary>True when the engine is not Gauge's to stop.</summary>
    public bool External => UsingUsersCopy || UsingBoardEngine;
    /// <summary>Why the engine is not available, for the Lighting tab; null when fine or simply absent.</summary>
    public string? Problem { get; private set; }
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public static string EngineFolder => Path.Combine(AppContext.BaseDirectory, "engine");
    static string EngineExe => Path.Combine(EngineFolder, "OpenRGB.exe");
    static string ConfigFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gauge", "openrgb");

    public OrgbClient? Start()
    {
        Problem = null;
        UsingUsersCopy = false;
        UsingBoardEngine = false;

        // Motherboard and RAM lighting set up a protected engine that starts with Windows; it owns every device.
        if (BoardLighting.Enabled)
        {
            for (int i = 0; i < 15; i++)
            {
                var board = OrgbClient.TryConnect(BoardLighting.Port, 1000);
                if (board != null) { UsingBoardEngine = true; return board; }
                Thread.Sleep(500);
            }
            Problem = "The motherboard and RAM lighting engine is not running. It starts with Windows, so restart, or turn it off and on again here.";
            return null;
        }

        // The user's own OpenRGB already owns the devices. Use it, never a second copy.
        if (UsersOpenRgbRunning())
        {
            var theirs = OrgbClient.TryConnect(UserDefaultPort, 1500);
            if (theirs != null) { UsingUsersCopy = true; return theirs; }
            Problem = "OpenRGB is already running without its SDK server. Turn the server on in OpenRGB, or close OpenRGB, then press Rescan.";
            return null;
        }

        if (!File.Exists(EngineExe)) return null; // a build without the engine; built-in drivers still work

        Directory.CreateDirectory(ConfigFolder);
        SeedConfig(ConfigFolder);
        int port = FreePort();
        var info = new ProcessStartInfo(EngineExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = EngineFolder,
        };
        foreach (string a in (ReadOnlySpan<string>)["--server", "--server-host", "127.0.0.1", "--server-port", port.ToString(), "--config", ConfigFolder, "--noautoconnect"])
            info.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(info);
            if (_process == null) { Problem = "The lighting engine did not start."; return null; }
            BindToGauge(_process);
        }
        catch (Exception ex)
        {
            Problem = "The lighting engine could not start: " + ex.Message;
            Log.Write("openrgb: start failed: " + ex);
            return null;
        }

        for (int i = 0; i < 40; i++)
        {
            if (_process.HasExited) { Problem = $"The lighting engine stopped unexpectedly (code {_process.ExitCode})."; return null; }
            var client = OrgbClient.TryConnect(port, 500);
            if (client != null) return client;
            Thread.Sleep(250);
        }
        Problem = "The lighting engine did not respond.";
        Stop();
        return null;
    }

    static bool UsersOpenRgbRunning()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            using (p)
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { } // an elevated copy hides its path; treat it as the user's
                if (path == null || !path.StartsWith(EngineFolder, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Turns off OpenRGB's detectors for devices Gauge drives itself. Leaves every other setting alone.</summary>
    internal static void SeedConfig(string folder)
    {
        string file = Path.Combine(folder, "OpenRGB.json");
        try
        {
            if (!File.Exists(file))
            {
                var entries = string.Join(", ", Array.ConvertAll(s_nativeDetectors, d => $"\"{d}\": false"));
                File.WriteAllText(file, "{ \"Detectors\": { \"detectors\": { " + entries + " } } }");
                return;
            }
            string text = File.ReadAllText(file), original = text;
            foreach (string d in s_nativeDetectors)
                text = Regex.Replace(text, "\"" + Regex.Escape(d) + "\"\\s*:\\s*true", $"\"{d}\": false");
            if (text != original) File.WriteAllText(file, text);
        }
        catch (Exception ex) { Log.Once("openrgb-config", ex); }
    }

    /// <summary>Puts the engine in a job that Windows closes, ending the engine, the moment Gauge exits for any reason.</summary>
    unsafe void BindToGauge(Process process)
    {
        _job = Native.CreateJobObjectW(0, null);
        if (_job == 0) return;
        byte* info = stackalloc byte[144]; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64
        new Span<byte>(info, 144).Clear();
        *(uint*)(info + 16) = 0x2000; // LimitFlags: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        Native.SetInformationJobObject(_job, 9, info, 144);
        Native.AssignProcessToJobObject(_job, process.Handle);
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        _process?.Dispose();
        _process = null;
        if (_job != 0) { Native.CloseHandle(_job); _job = 0; }
    }

    public void Dispose() => Stop();
}

/// <summary>A device controlled through OpenRGB.</summary>
internal sealed class OrgbDevice : LightDevice
{
    readonly Lighting _owner;
    readonly string _key;
    public OrgbController Controller;

    static readonly string[] s_kinds =
    [
        "Motherboard", "Memory", "Graphics card", "Cooler", "LED strip", "Keyboard", "Mouse", "Mouse mat", "Headset", "Headset stand",
        "Gamepad", "Light", "Speaker", "Virtual", "Storage", "Case", "Microphone", "Accessory", "Keypad", "Laptop", "Monitor",
    ];

    public OrgbDevice(Lighting owner, OrgbController c, bool usersCopy)
    {
        _owner = owner;
        Controller = c;
        Kind = c.Type >= 0 && c.Type < s_kinds.Length ? s_kinds[c.Type] : "Device";
        Name = string.IsNullOrWhiteSpace(c.Name) ? "Lighting device" : c.Name.Trim();
        string vendor = c.Vendor.Trim();
        Detail = (vendor.Length > 0 && !Name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? vendor + "  ·  " : "") + (usersCopy ? "via your OpenRGB" : "via OpenRGB");
        if (c.UsbId is { } usb) { VendorId = usb.Vendor; ProductId = usb.Product; }
        _key = "orgb." + StableId(c);

        Modes = Array.ConvertAll(c.Modes, m => m.Name);
        ZoneNames = Array.ConvertAll(c.Zones, z => z.Name);

        // Unlike the native keyboard, OpenRGB reports what the device is showing; start from that.
        State.Zones = new Color[c.Zones.Length];
        int led = 0;
        for (int z = 0; z < c.Zones.Length; z++)
        {
            State.Zones[z] = led < c.Colours.Length && c.Zones[z].LedCount > 0 ? Unbright(OrgbClient.Unpack(c.Colours[led])) : Color.White;
            led += (int)c.Zones[z].LedCount;
        }
        State.Mode = Math.Clamp(c.ActiveMode, 0, Math.Max(0, c.Modes.Length - 1));
        if (c.Modes.Length > 0)
        {
            var m = c.Modes[State.Mode];
            State.Speed = m.Has(OrgbMode.HasSpeed) ? 1 + (int)MathF.Round(3 * Fraction(m.Speed, m.SpeedMin, m.SpeedMax)) : 2;
            State.Brightness = m.Has(OrgbMode.HasBrightness) && Fraction(m.Brightness, m.BrightnessMin, m.BrightnessMax) < 0.75f ? 1 : 2;
        }
        Applied = true;
    }

    /// <summary>Survives OpenRGB renumbering its list: built from what identifies the hardware, not its position.</summary>
    static string StableId(OrgbController c)
    {
        string basis = $"{c.Vendor}|{c.Name}|{c.Serial}|{c.Location}";
        uint hash = 2166136261;
        foreach (char ch in basis) { hash ^= ch; hash *= 16777619; }
        return hash.ToString("x8");
    }

    static float Fraction(uint value, uint min, uint max) => max == min ? 1 : Math.Clamp(((float)value - min) / ((float)max - min), 0, 1);

    /// <summary>Colours are kept at full brightness; scale a dimmed reading back up so the picker shows the hue.</summary>
    static Color Unbright(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max == 0) return Color.White;
        float k = 255f / max;
        return Color.FromArgb(255, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }

    public override string Key => _key;
    public override bool ModeUsesColours(int mode) => mode >= 0 && mode < Controller.Modes.Length && Controller.Modes[mode].UsesColours;
    public override bool ModeHasSpeed(int mode) => mode >= 0 && mode < Controller.Modes.Length && Controller.Modes[mode].Has(OrgbMode.HasSpeed);

    /// <summary>The mode picking a colour switches to: per-LED "Direct" if there is one, then any colour mode.</summary>
    public override int ColourMode
    {
        get
        {
            var modes = Controller.Modes;
            int i = Array.FindIndex(modes, m => m.Has(OrgbMode.HasPerLedColour) && m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = Array.FindIndex(modes, m => m.Name.Equals("Static", StringComparison.OrdinalIgnoreCase) && m.UsesColours);
            if (i < 0) i = Array.FindIndex(modes, m => m.Has(OrgbMode.HasPerLedColour));
            if (i < 0) i = Array.FindIndex(modes, m => m.UsesColours);
            return Math.Max(0, i);
        }
    }

    public override string Note => "Controlled through " + (Detail.EndsWith("your OpenRGB") ? "your OpenRGB." : "Gauge's built-in OpenRGB engine.");

    public override void Send(LightState state) => _owner.SendOpenRgb(this, state);

    /// <summary>Turns a Gauge state into OpenRGB mode and colour updates. Caller holds the client lock.</summary>
    internal void Apply(OrgbClient client, LightState s)
    {
        var c = Controller;
        if (c.Modes.Length == 0) return;
        int index = Math.Clamp(s.Mode, 0, c.Modes.Length - 1);
        var colours = s.Zones;

        if (s.Brightness <= 0)
        {
            int off = Array.FindIndex(c.Modes, m => m.Name.Equals("Off", StringComparison.OrdinalIgnoreCase));
            if (off >= 0) { client.UpdateMode(c.Index, off, c.Modes[off].Clone()); return; }
            index = ColourMode; // no "Off" mode: show black
            colours = new Color[s.Zones.Length];
            Array.Fill(colours, Color.Black);
        }

        var mode = c.Modes[index].Clone();
        float level = s.Brightness >= 2 ? 1f : 0.5f;
        if (mode.Has(OrgbMode.HasBrightness))
            mode.Brightness = (uint)MathF.Round(mode.BrightnessMin + (mode.BrightnessMax - (float)mode.BrightnessMin) * level);
        if (mode.Has(OrgbMode.HasSpeed))
            mode.Speed = (uint)MathF.Round(mode.SpeedMin + (mode.SpeedMax - (float)mode.SpeedMin) * (Math.Clamp(s.Speed, 1, 4) - 1) / 3f);
        float scale = !mode.Has(OrgbMode.HasBrightness) && s.Brightness == 1 ? 0.55f : 1f;
        uint Scaled(Color col) => OrgbClient.Pack(Color.FromArgb(255, (int)(col.R * scale), (int)(col.G * scale), (int)(col.B * scale)));

        if (mode.Has(OrgbMode.HasPerLedColour))
        {
            mode.ColourMode = OrgbMode.ColoursPerLed;
            client.UpdateMode(c.Index, index, mode);
            var leds = new uint[c.Colours.Length];
            int led = 0;
            for (int z = 0; z < c.Zones.Length; z++)
            {
                uint value = z < colours.Length ? Scaled(colours[z]) : 0;
                for (int k = 0; k < c.Zones[z].LedCount && led < leds.Length; k++) leds[led++] = value;
            }
            if (leds.Length > 0) client.SetLeds(c.Index, leds);
        }
        else if (mode.Has(OrgbMode.HasModeColour))
        {
            mode.ColourMode = OrgbMode.ColoursModeSpecific;
            int n = mode.Colours.Length > 0 ? mode.Colours.Length : (int)Math.Max(1, mode.ColoursMin);
            mode.Colours = new uint[n];
            for (int i = 0; i < n; i++) mode.Colours[i] = colours.Length > 0 ? Scaled(colours[i % colours.Length]) : 0;
            client.UpdateMode(c.Index, index, mode);
        }
        else
        {
            client.UpdateMode(c.Index, index, mode);
        }
        c.ActiveMode = index;
    }
}

internal static unsafe partial class Native
{
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
