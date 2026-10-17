using System.Diagnostics;
using HidSharp;

namespace Gauge;

/// <summary>What a lighting device should show. Zone colours are stored at full brightness.</summary>
internal sealed class LightState
{
    public Color[] Zones = [];
    /// <summary>0 off, 1 low, 2 high.</summary>
    public int Brightness = 2;
    /// <summary>Index into the device's <see cref="LightDevice.Modes"/>.</summary>
    public int Mode;
    /// <summary>1 slowest to 4 fastest; ignored by modes without a speed.</summary>
    public int Speed = 2;

    public LightState Clone() => new() { Zones = (Color[])Zones.Clone(), Brightness = Brightness, Mode = Mode, Speed = Speed };
}

/// <summary>One controllable lighting device: a built-in driver, or a device reached through OpenRGB.</summary>
internal abstract class LightDevice
{
    public string Kind = "Device", Name = "", Detail = "";
    public string[] ZoneNames = [];
    /// <summary>The device's effects, in the order its driver numbers them.</summary>
    public string[] Modes = [];
    public int VendorId, ProductId;
    public LightState State = new();
    /// <summary>Why the last send failed, or null.</summary>
    public string? Error;
    /// <summary>True once the shown state is known to match the hardware.</summary>
    public bool Applied;

    /// <summary>Settings prefix, stable across runs.</summary>
    public abstract string Key { get; }

    /// <summary>Writes the state to the hardware. Throws on failure.</summary>
    public abstract void Send(LightState state);

    /// <summary>True when the mode draws from the zone colours; others run colours of their own.</summary>
    public virtual bool ModeUsesColours(int mode) => true;
    public virtual bool ModeHasSpeed(int mode) => false;
    /// <summary>The mode that choosing a colour switches to, when the current one ignores colours.</summary>
    public virtual int ColourMode => 0;
    /// <summary>Called on the lighting thread once sends to this device have stopped for a moment, for drivers that persist settings.</summary>
    public virtual void Settle() { }
    /// <summary>A line for the Lighting tab about how this device is driven.</summary>
    public virtual string Note => Applied ? "Applied to the device." : "This device cannot report its current lighting. What is shown here is sent when you change a setting.";
}

/// <summary>
/// The 4-zone RGB keyboard in Lenovo Legion, LOQ and IdeaPad Gaming laptops (ITE 8295).
/// One 33-byte HID feature report sets everything; the device cannot be read back.
/// </summary>
internal sealed class Lenovo4ZoneKeyboard : LightDevice
{
    const int IteVendor = 0x048D, ReportLength = 33;
    const uint LightingUsage = 0xFF8900CC; // usage page 0xFF89, usage 0xCC
    static readonly int[] s_productIds = [0xC955, 0xC963, 0xC965, 0xC973, 0xC975, 0xC983, 0xC984, 0xC985, 0xC993, 0xC994, 0xC995];
    static readonly byte[] s_effectBytes = [0x01, 0x03, 0x04, 0x06]; // static, breathing, wave, smooth

    readonly HidDevice _hid;

    Lenovo4ZoneKeyboard(HidDevice hid)
    {
        _hid = hid;
        VendorId = hid.VendorID;
        ProductId = hid.ProductID;
        Kind = "Keyboard";
        Name = "Lenovo 4-zone keyboard";
        Detail = $"ITE 8295  ·  {hid.VendorID:X4}:{hid.ProductID:X4}";
        ZoneNames = ["Zone 1", "Zone 2", "Zone 3", "Zone 4"];
        Modes = ["Static", "Breathing", "Wave", "Smooth"];
        State.Zones = [Color.White, Color.White, Color.White, Color.White];
    }

    public override string Key => "kbd4";
    public override bool ModeUsesColours(int mode) => mode is 0 or 1;
    public override bool ModeHasSpeed(int mode) => mode is 1 or 2 or 3;

    /// <summary>
    /// Matches only on all three of: a known USB id, the lighting usage, and the exact report length.
    /// Nothing is written here.
    /// </summary>
    public static IEnumerable<LightDevice> Detect()
    {
        foreach (var hid in DeviceList.Local.GetHidDevices(IteVendor))
        {
            if (Array.IndexOf(s_productIds, hid.ProductID) < 0) continue;
            bool isLighting;
            try
            {
                isLighting = hid.GetMaxFeatureReportLength() == ReportLength && HasUsage(hid, LightingUsage);
            }
            catch { continue; }
            if (isLighting)
            {
                yield return new Lenovo4ZoneKeyboard(hid);
                yield break; // one keyboard per laptop
            }
        }
    }

    static bool HasUsage(HidDevice hid, uint usage)
    {
        foreach (var item in hid.GetReportDescriptor().DeviceItems)
            foreach (uint u in item.Usages.GetAllValues())
                if (u == usage) return true;
        return false;
    }

    /// <summary>The feature report for a state. Pure, so it can be checked without hardware.</summary>
    public static byte[] BuildReport(LightState s)
    {
        var r = new byte[ReportLength];
        r[0] = 0xCC; // report id
        r[1] = 0x16;
        bool off = s.Brightness <= 0;
        int mode = Math.Clamp(s.Mode, 0, s_effectBytes.Length - 1);
        r[2] = off ? s_effectBytes[0] : s_effectBytes[mode];
        r[3] = (byte)Math.Clamp(s.Speed, 1, 4);
        r[4] = (byte)(s.Brightness >= 2 ? 2 : 1);
        if (!off && mode is 0 or 1)
        {
            for (int z = 0; z < 4 && z < s.Zones.Length; z++)
            {
                r[5 + z * 3] = s.Zones[z].R;
                r[6 + z * 3] = s.Zones[z].G;
                r[7 + z * 3] = s.Zones[z].B;
            }
        }
        if (!off && mode == 2) r[18] = 1; // wave runs left to right
        return r;
    }

    public override void Send(LightState state)
    {
        using var stream = _hid.Open();
        stream.SetFeature(BuildReport(state));
    }
}

/// <summary>A connected device from a maker of RGB hardware that no driver handles yet.</summary>
internal sealed record UnclaimedDevice(string Vendor, string Product, int VendorId, int ProductId, string Interfaces);

/// <summary>
/// Finds lighting devices, from Gauge's own drivers and then through the bundled OpenRGB engine,
/// and applies states to them off the UI thread.
/// </summary>
internal sealed class Lighting : IDisposable
{
    /// <summary>USB vendor ids of companies that make RGB hardware. Used only to list candidates; nothing is written to them.</summary>
    static readonly Dictionary<int, string> s_rgbVendors = new()
    {
        [0x1B1C] = "Corsair", [0x1E71] = "NZXT", [0x1532] = "Razer", [0x046D] = "Logitech", [0x1038] = "SteelSeries",
        [0x0B05] = "ASUS", [0x1462] = "MSI", [0x0DB0] = "MSI", [0x1044] = "Gigabyte", [0x048D] = "ITE", [0x26CE] = "ASRock",
        [0x0951] = "HyperX", [0x03F0] = "HP / HyperX", [0x2516] = "Cooler Master", [0x0CF2] = "Lian Li", [0x264A] = "Thermaltake",
        [0x258A] = "Glorious", [0x1E7D] = "Roccat", [0x31E3] = "Wooting", [0x3842] = "EVGA", [0x3434] = "Keychron",
        [0x04D9] = "Holtek", [0x0C45] = "Sonix", [0x320F] = "Glorious", [0x3633] = "Deepcool", [0x1A2C] = "China Resource",
        [0x17EF] = "Lenovo", [0x187C] = "Alienware", [0x2433] = "ASETEK", [0x1B80] = "Phanteks", [0x152D] = "JMicron",
    };

    /// <summary>Devices from those vendors known to have no lighting, so they are not listed as candidates.</summary>
    static readonly HashSet<(int, int)> s_notLighting =
    [
        (0x048D, 0xC986), // Lenovo laptop keyboard input controller; its lighting is the separate C98x device
    ];

    // OpenRGB is not listed: Gauge either drives it or uses the user's copy of it.
    static readonly string[] s_rivals = ["iCUE", "LenovoVantage", "LegionZone", "SignalRgb", "ArmouryCrate", "LightingService", "RzSynapse", "Razer Synapse", "lghub", "NZXT CAM", "RGBFusion", "MysticLight"];

    public readonly object Sync = new();
    public readonly List<LightDevice> Devices = new();
    /// <summary>Devices that may have lighting but have no driver. Listed so a driver can be written for them.</summary>
    public readonly List<UnclaimedDevice> Unclaimed = new();
    public volatile bool Scanned;
    /// <summary>True while OpenRGB is still looking for devices.</summary>
    public volatile bool EngineBusy;
    /// <summary>A problem with the OpenRGB engine to show the user, or null.</summary>
    public string? EngineProblem;
    /// <summary>A running vendor lighting app that will fight Gauge for the device, or null.</summary>
    public string? Rival;
    public bool RestoreOnLaunch;
    /// <summary>Motherboard and RAM lighting is set up, and whether its protected engine is older than Gauge's.</summary>
    public bool BoardEnabled, BoardNeedsRefresh;
    public event Action? Changed;

    readonly object _clientLock = new();
    readonly OrgbEngine _engine = new();
    OrgbClient? _client;
    readonly AutoResetEvent _wake = new(false);
    readonly HashSet<LightDevice> _dirty = new();
    Thread? _worker;
    volatile bool _stop, _scanning;

    /// <summary>Scans on a background thread, then restores saved lighting if the user asked for that.</summary>
    public void ScanAsync(int delayMs = 0)
    {
        if (_scanning) return;
        _scanning = true;
        new Thread(() =>
        {
            try
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
                if (_stop) return;
                Scan();
                if (RestoreOnLaunch)
                    lock (Sync)
                        foreach (var d in Devices) if (Settings.Get($"light.{d.Key}.zones") != null) Queue(d);
                Changed?.Invoke();
            }
            finally { _scanning = false; }
        }) { IsBackground = true, Name = "LightingScan" }.Start();
    }

    /// <summary>
    /// Built-in drivers first, published at once; then OpenRGB, which takes a few seconds.
    /// Only reads: sends nothing to any device.
    /// </summary>
    public void Scan()
    {
        var native = new List<LightDevice>();
        try { native.AddRange(Lenovo4ZoneKeyboard.Detect()); }
        catch (Exception ex) { Log.Once("lighting-scan", ex); }
        try { native.AddRange(QmkViaKeyboard.Detect()); }
        catch (Exception ex) { Log.Once("lighting-scan-qmk", ex); }

        string? rival = null;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string name = p.ProcessName;
                p.Dispose();
                if (rival == null && Array.Exists(s_rivals, r => name.StartsWith(r, StringComparison.OrdinalIgnoreCase))) rival = name;
            }
        }
        catch { }

        foreach (var d in native) Load(d);
        var unclaimed = FindUnclaimed(native);
        lock (Sync)
        {
            Devices.RemoveAll(d => d is not OrgbDevice);
            Devices.InsertRange(0, native);
            Unclaimed.Clear();
            Unclaimed.AddRange(unclaimed);
            Rival = rival;
            RestoreOnLaunch = Settings.Get("light.restore") == "1";
            BoardEnabled = BoardLighting.Enabled;
            BoardNeedsRefresh = BoardLighting.NeedsRefresh;
            EngineBusy = true;
        }
        Scanned = true;
        Changed?.Invoke();
        Log.Write($"lighting: {native.Count} built-in device(s){(native.Count > 0 ? ": " + string.Join(", ", native.ConvertAll(d => $"{d.Name} [{d.Detail}]")) : "")}{(rival != null ? $", rival app {rival}" : "")}");

        ScanOpenRgb(native);
    }

    void ScanOpenRgb(List<LightDevice> native)
    {
        var found = new List<OrgbDevice>();
        string? problem = null;
        lock (_clientLock)
        {
            // Motherboard and RAM lighting switched on or off since the last scan: change engines.
            if (_client != null && BoardLighting.Enabled != _engine.UsingBoardEngine) DropClient();
            if (_client == null) _client = _engine.Start();
            problem = _engine.Problem;
            if (_client != null)
            {
                try
                {
                    // OpenRGB detects in the background and its list grows as it goes. Wait until the
                    // count has held still for a few seconds, and give an empty list longer to fill.
                    var clock = Stopwatch.StartNew();
                    int count = -1, steady = 0;
                    while (clock.Elapsed < TimeSpan.FromSeconds(45))
                    {
                        int n = _client.Count();
                        steady = n == count ? steady + 1 : 0;
                        count = n;
                        if (steady >= 4 && (count > 0 || clock.Elapsed > TimeSpan.FromSeconds(10))) break;
                        Thread.Sleep(750);
                    }
                    for (int i = 0; i < count; i++)
                    {
                        var c = _client.Controller(i);
                        if (c.Modes.Length == 0) continue;
                        if (c.UsbId is { } usb && native.Exists(d => d.VendorId == usb.Vendor && d.ProductId == usb.Product)) continue; // a built-in driver has it
                        found.Add(new OrgbDevice(this, c, _engine.UsingUsersCopy));
                    }
                }
                catch (Exception ex)
                {
                    problem = "Lost contact with the lighting engine. Press Rescan to try again.";
                    Log.Write("openrgb: scan failed: " + ex.Message);
                    DropClient();
                }
                // Nothing to drive: let Gauge's own engine go rather than hold its memory. Never stop the user's copy.
                if (found.Count == 0 && _client != null && !_engine.External) DropClient();
            }
        }

        foreach (var d in found) Load(d);
        lock (Sync)
        {
            Devices.RemoveAll(d => d is OrgbDevice);
            Devices.AddRange(found);
            Unclaimed.RemoveAll(u => found.Exists(f => f.VendorId == u.VendorId && f.ProductId == u.ProductId));
            EngineProblem = problem;
            EngineBusy = false;
        }
        string engine = _engine.UsingBoardEngine ? "protected engine (motherboard and RAM access)" : _engine.UsingUsersCopy ? "user's own OpenRGB" : "Gauge's engine (USB devices)";
        Log.Write($"openrgb: {found.Count} device(s) via {engine}{(found.Count > 0 ? ": " + string.Join(", ", found.ConvertAll(d => $"{d.Kind} {d.Name} [{d.VendorId:X4}:{d.ProductId:X4}]")) : "")}{(problem != null ? ", " + problem : "")}");
        lock (Sync)
        {
            foreach (var u in Unclaimed) Log.Write($"lighting: no driver for {u.Vendor} {u.Product} [{u.VendorId:X4}:{u.ProductId:X4}] {u.Interfaces}");
            if (Rival != null) Log.Write($"lighting: {Rival} is running and may hold devices");
        }
        Changed?.Invoke();
    }

    /// <summary>Closes the connection and stops Gauge's engine. Caller holds the client lock.</summary>
    void DropClient()
    {
        _client?.Dispose();
        _client = null;
        _engine.Stop();
    }

    /// <summary>Sends a state to a device through OpenRGB. Called on the lighting worker thread.</summary>
    internal void SendOpenRgb(OrgbDevice device, LightState state)
    {
        lock (_clientLock)
        {
            if (_client == null) throw new IOException("the lighting engine is not running; press Rescan");
            try { device.Apply(_client, state); }
            catch (IOException) { DropClient(); throw; }
        }
    }

    /// <summary>
    /// Developer check: sends states to every OpenRGB device and reads them back from OpenRGB,
    /// proving the mode and colour packets are understood. Meant for OpenRGB's simulated devices.
    /// </summary>
    internal string RoundTripTest()
    {
        var sb = new System.Text.StringBuilder();
        List<OrgbDevice> devices;
        lock (Sync) devices = Devices.OfType<OrgbDevice>().ToList();
        Color[] palette = [Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 255, 0), Color.FromArgb(255, 0, 0, 255), Color.FromArgb(255, 255, 160, 0)];
        foreach (var d in devices)
        {
            try
            {
                // 1. Per-zone colours in the colour mode
                var s = d.State.Clone();
                s.Mode = d.ColourMode;
                s.Brightness = 2;
                for (int z = 0; z < s.Zones.Length; z++) s.Zones[z] = palette[z % palette.Length];
                d.Send(s);
                Thread.Sleep(150);
                OrgbController back;
                lock (_clientLock) back = _client!.Controller(d.Controller.Index);
                int led = 0, good = 0, checkedZones = 0;
                for (int z = 0; z < back.Zones.Length; z++)
                {
                    if (back.Zones[z].LedCount > 0 && led < back.Colours.Length)
                    {
                        checkedZones++;
                        var got = OrgbClient.Unpack(back.Colours[led]);
                        var want = s.Zones[z];
                        if (!d.Controller.Modes[s.Mode].Has(OrgbMode.HasPerLedColour) || (got.R == want.R && got.G == want.G && got.B == want.B)) good++;
                    }
                    led += (int)back.Zones[z].LedCount;
                }
                sb.AppendLine($"{d.Kind} '{d.Name}': colour mode '{d.Modes[s.Mode]}' -> active '{back.Modes[back.ActiveMode].Name}', zones matching {good}/{checkedZones}");

                // 2. An effect with speed, when the device has one
                int fx = Enumerable.Range(0, d.Modes.Length).FirstOrDefault(i => d.ModeHasSpeed(i) && i != s.Mode, -1);
                if (fx >= 0)
                {
                    var e = s.Clone();
                    e.Mode = fx;
                    e.Speed = 4;
                    d.Send(e);
                    Thread.Sleep(150);
                    lock (_clientLock) back = _client!.Controller(d.Controller.Index);
                    var m = back.Modes[back.ActiveMode];
                    sb.AppendLine($"    effect '{d.Modes[fx]}' -> active '{m.Name}', speed {m.Speed} (range {m.SpeedMin}..{m.SpeedMax}, sent fastest)");
                }
            }
            catch (Exception ex) { sb.AppendLine($"{d.Kind} '{d.Name}': FAILED {ex.Message}"); }
        }
        return sb.Length > 0 ? sb.ToString() : "no OpenRGB devices";
    }

    /// <summary>
    /// Read-only: lists connected HID devices from RGB hardware makers that no driver claimed,
    /// with each interface's usage and report sizes, which is what a new driver is written against.
    /// </summary>
    static List<UnclaimedDevice> FindUnclaimed(List<LightDevice> claimed)
    {
        var result = new List<UnclaimedDevice>();
        try
        {
            var groups = new SortedDictionary<(int Vendor, int Product), List<HidDevice>>();
            foreach (var hid in DeviceList.Local.GetHidDevices())
            {
                if (!s_rgbVendors.ContainsKey(hid.VendorID)) continue;
                if (s_notLighting.Contains((hid.VendorID, hid.ProductID))) continue;
                if (claimed.Exists(c => c.VendorId == hid.VendorID && c.ProductId == hid.ProductID)) continue;
                var key = (hid.VendorID, hid.ProductID);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<HidDevice>();
                list.Add(hid);
            }
            foreach (var (key, list) in groups)
            {
                string product = "Unnamed device";
                var parts = new List<string>();
                foreach (var hid in list)
                {
                    try { if (hid.GetProductName() is { Length: > 0 } name) product = name.Trim(); } catch { }
                    string usage = "????:????";
                    try
                    {
                        foreach (var item in hid.GetReportDescriptor().DeviceItems)
                            foreach (uint u in item.Usages.GetAllValues()) { usage = $"{u >> 16:X4}:{u & 0xFFFF:X4}"; break; }
                    }
                    catch { }
                    int input = 0, output = 0, feature = 0;
                    try { input = hid.GetMaxInputReportLength(); output = hid.GetMaxOutputReportLength(); feature = hid.GetMaxFeatureReportLength(); } catch { }
                    string part = $"{usage} in{input} out{output} feat{feature}";
                    // A QMK raw HID interface: say what it answers, which is what a QMK driver needs.
                    if (QmkViaKeyboard.Describe(hid) is { } qmk) part += $" [{qmk}]";
                    if (!parts.Contains(part)) parts.Add(part);
                }
                result.Add(new UnclaimedDevice(s_rgbVendors[key.Vendor], product, key.Vendor, key.Product, string.Join("; ", parts)));
            }
        }
        catch (Exception ex) { Log.Once("lighting-unclaimed", ex); }
        return result;
    }

    /// <summary>Plain-text summary of lighting hardware, for a tester to send back.</summary>
    public string DeviceReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Gauge {Program.Version} lighting report");
        lock (Sync)
        {
            sb.AppendLine($"Supported ({Devices.Count}):");
            foreach (var d in Devices)
            {
                string via = d is OrgbDevice o ? $" via OpenRGB, location {o.Controller.Location}" : d is QmkViaKeyboard ? $" via VIA ({d.Detail})" : "";
                sb.AppendLine($"  {d.Kind}: {d.Name} [{d.VendorId:X4}:{d.ProductId:X4}]{via}, {d.ZoneNames.Length} zone(s), modes: {string.Join(", ", d.Modes)}");
            }
            sb.AppendLine($"No driver yet ({Unclaimed.Count}):");
            foreach (var u in Unclaimed) sb.AppendLine($"  {u.Vendor} {u.Product} [{u.VendorId:X4}:{u.ProductId:X4}]  {u.Interfaces}");
            if (EngineProblem != null) sb.AppendLine($"Lighting engine: {EngineProblem}");
            if (Rival != null) sb.AppendLine($"Lighting app running: {Rival}");
        }
        return sb.ToString().TrimEnd();
    }

    static void Load(LightDevice d)
    {
        string prefix = $"light.{d.Key}.";
        if (Settings.Get(prefix + "zones") is { } zones)
        {
            string[] parts = zones.Split(',');
            for (int i = 0; i < parts.Length && i < d.State.Zones.Length; i++)
                if (Settings.ParseColor(parts[i]) is Color c) d.State.Zones[i] = c;
        }
        // Stored by name, so a device that renumbers its modes keeps the right one.
        if (Settings.Get(prefix + "effect") is { } effect)
        {
            int mode = Array.FindIndex(d.Modes, m => m.Equals(effect, StringComparison.OrdinalIgnoreCase));
            if (mode >= 0) d.State.Mode = mode;
        }
        if (int.TryParse(Settings.Get(prefix + "brightness"), out int b)) d.State.Brightness = Math.Clamp(b, 0, 2);
        if (int.TryParse(Settings.Get(prefix + "speed"), out int s)) d.State.Speed = Math.Clamp(s, 1, 4);
    }

    public void Save(LightDevice d)
    {
        string prefix = $"light.{d.Key}.";
        LightState s;
        string? mode;
        lock (Sync)
        {
            s = d.State.Clone();
            mode = s.Mode >= 0 && s.Mode < d.Modes.Length ? d.Modes[s.Mode] : null;
        }
        Settings.Set(
            (prefix + "zones", string.Join(",", Array.ConvertAll(s.Zones, c => Settings.Hex(c).TrimStart('#')))),
            (prefix + "effect", mode),
            (prefix + "brightness", s.Brightness.ToString()),
            (prefix + "speed", s.Speed.ToString()),
            ("light.restore", RestoreOnLaunch ? "1" : "0"));
    }

    /// <summary>Sends the device's current state soon. Rapid calls collapse into the latest state.</summary>
    public void Apply(LightDevice d)
    {
        lock (Sync) Queue(d);
    }

    void Queue(LightDevice d)
    {
        _dirty.Add(d);
        if (_worker == null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Lighting" };
            _worker.Start();
        }
        _wake.Set();
    }

    void Work()
    {
        var batch = new List<(LightDevice Device, LightState State)>();
        var unsettled = new HashSet<LightDevice>();
        while (!_stop)
        {
            // Devices that persist their settings do so once changes stop, not on every slider step.
            if (!_wake.WaitOne(unsettled.Count > 0 ? 1500 : Timeout.Infinite))
            {
                foreach (var d in unsettled)
                {
                    try { d.Settle(); }
                    catch (Exception ex) { Log.Once("lighting-settle-" + d.Key, ex); }
                }
                unsettled.Clear();
                continue;
            }
            if (_stop) break;
            batch.Clear();
            lock (Sync)
            {
                foreach (var d in _dirty) batch.Add((d, d.State.Clone()));
                _dirty.Clear();
            }
            foreach (var (device, state) in batch)
            {
                string? error = null;
                try { device.Send(state); }
                catch (Exception ex) { error = ex.Message; Log.Once("lighting-send-" + device.Key, ex); }
                lock (Sync) { device.Error = error; device.Applied |= error == null; }
                if (error == null) unsettled.Add(device);
            }
            Changed?.Invoke();
            Thread.Sleep(40); // a slider drag becomes at most ~25 writes a second
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _worker?.Join(500);
        // No lock: a scan may hold it for up to 45 s. Stopping the engine breaks that scan's
        // connection, and the job object would end the engine on exit regardless.
        _engine.Stop();
        _client?.Dispose();
    }
}
