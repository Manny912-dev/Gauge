using System.Diagnostics;
using HidSharp;

namespace Gauge;

/// <summary>What the Kraken's screen shows as its number.</summary>
internal enum LcdReading { LiquidTemp, CpuTemp, CpuLoad, CpuClock, GpuTemp, GpuLoad, GpuClock }

/// <summary>The screen's settings, mirroring NZXT CAM's "Single Infographic" page. Stored as lcd.* in settings.ini.</summary>
internal sealed class LcdSettings
{
    /// <summary>False: the Kraken shows its own built-in screen and Gauge leaves the cooler alone.</summary>
    public bool Drive;
    /// <summary>False: the radial ring. True: the full-screen gradient ("Full Fill").</summary>
    public bool Fill;
    public LcdReading Reading = LcdReading.LiquidTemp;
    public Color Color1 = Color.FromArgb(255, 0x6B, 0x00, 0xDE), Color2 = Color.FromArgb(255, 0xD6, 0x00, 0xBF);
    public Color Background = Color.Black, Ink = Color.White;
    public bool Logo = true;
    /// <summary>0..100, sent without re-uploading the image.</summary>
    public int Brightness = 50;
    /// <summary>0, 90, 180 or 270 degrees clockwise; 0 is upright as NZXT CAM shows it by default.</summary>
    public int Rotation;

    public LcdSettings Clone() => (LcdSettings)MemberwiseClone();

    public static LcdSettings Load()
    {
        var s = new LcdSettings();
        s.Drive = Gauge.Settings.Get("lcd.drive") == "1";
        s.Fill = Gauge.Settings.Get("lcd.style") == "fill";
        if (Enum.TryParse<LcdReading>(Gauge.Settings.Get("lcd.reading"), out var r)) s.Reading = r;
        if (Gauge.Settings.ParseColor(Gauge.Settings.Get("lcd.color1")) is Color c1) s.Color1 = c1;
        if (Gauge.Settings.ParseColor(Gauge.Settings.Get("lcd.color2")) is Color c2) s.Color2 = c2;
        s.Logo = Gauge.Settings.Get("lcd.logo") != "0";
        if (int.TryParse(Gauge.Settings.Get("lcd.brightness"), out int b)) s.Brightness = Math.Clamp(b, 0, 100);
        if (int.TryParse(Gauge.Settings.Get("lcd.rotation"), out int rot) && rot is 0 or 90 or 180 or 270) s.Rotation = rot;
        return s;
    }

    public void Save() => Gauge.Settings.Set(
        ("lcd.drive", Drive ? "1" : "0"), ("lcd.style", Fill ? "fill" : "ring"), ("lcd.reading", Reading.ToString()),
        ("lcd.color1", Gauge.Settings.Hex(Color1)), ("lcd.color2", Gauge.Settings.Hex(Color2)), ("lcd.logo", Logo ? "1" : "0"),
        ("lcd.brightness", Brightness.ToString()), ("lcd.rotation", Rotation.ToString()));
}

/// <summary>How Gauge reaches the cooler: 64-byte HID reports for commands, a bulk pipe for pixels.</summary>
internal interface IKrakenLink : IDisposable
{
    void Write(byte[] report);
    /// <summary>The next 64-byte input report, or null after <paramref name="timeoutMs"/>.</summary>
    byte[]? Read(int timeoutMs);
    void Bulk(ReadOnlySpan<byte> data);
}

/// <summary>A refusal from the cooler: the result code from byte 14 of its reply.</summary>
internal sealed class KrakenResultException(byte code) : IOException($"the cooler answered '{Describe(code)}' ({code})")
{
    public byte Code { get; } = code;

    static string Describe(byte code) => code switch
    {
        2 => "currently transferring graphics", 3 => "invalid bucket", 4 => "bucket already exists", 5 => "bucket slots overlap another bucket",
        6 => "bucket slots are invalid", 7 => "invalid asset id or type", 8 => "bucket does not exist", 9 => "bucket is on screen and cannot be changed",
        10 => "bucket has no graphic", 11 => "bucket graphic type is none", 16 => "no transfer was started", 17 => "invalid display output",
        21 => "not ready for a streamed frame", 22 => "unsupported streaming format", _ => "error",
    };
}

/// <summary>
/// The Kraken Z3 LCD protocol over an <see cref="IKrakenLink"/>: commands are <c>[cmd, sub, …]</c> padded to
/// 64 bytes, replies come back as <c>[cmd+1, sub, …]</c> with the result at byte 14 (1 = success), mixed in
/// with the cooler's own status reports. Frames are 320×320 RGBA written to one of two RAM "buckets" in turn,
/// so the one on screen is never the one being written. Never touches flash.
/// </summary>
internal sealed class KrakenSession : IDisposable
{
    public const int Size = 320, FrameBytes = Size * Size * 4, Slots = (FrameBytes + 1023) / 1024; // 400 slots of 1 KiB
    static readonly byte[] s_magic = [0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10];
    const byte AssetRaw = 0x02, OutputFirmware = 0x02, OutputBucket = 0x04;
    const int Buckets = 16;

    readonly IKrakenLink _link;
    readonly int[] _start = new int[2];
    readonly bool[] _ready = new bool[2];
    readonly byte[] _bucket = new byte[2];
    int _shown = -1; // which of our two buckets is on screen, or -1
    public int BrightnessOnDevice { get; private set; } = -1;

    public KrakenSession(IKrakenLink link) => _link = link;

    // ------------------------------------------------------------------ messages

    byte[] Request(byte[] command, int timeoutMs = 1500)
    {
        var report = new byte[64];
        command.CopyTo(report, 0);
        _link.Write(report);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            var r = _link.Read(Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds));
            if (r == null) break;
            if (r[0] == command[0] + 1 && r[1] == command[1]) return r;
            if (r[0] == 0xFF && r[1] == 0x02 && r[14] == command[0]) throw new IOException($"the cooler refused command {command[0]:X2} {command[1]:X2}");
            // anything else is the cooler's own status traffic
        }
        throw new TimeoutException($"the cooler did not answer command {command[0]:X2} {command[1]:X2}");
    }

    static byte[] Ok(byte[] reply)
    {
        if (reply[14] != 1) throw new KrakenResultException(reply[14]);
        return reply;
    }

    void Send(byte[] command)
    {
        var report = new byte[64];
        command.CopyTo(report, 0);
        _link.Write(report);
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Cancels any half-finished transfer, reads the screen's state and places two 400-slot buckets clear of any others.</summary>
    public void Init()
    {
        try { Request([0x36, 0x03]); } catch (IOException) { } // CAM does this too; the result does not matter
        var info = Request([0x30, 0x01]);
        BrightnessOnDevice = info[24];

        // The two highest buckets are ours; place them after everything else in use.
        _bucket[0] = Buckets - 2;
        _bucket[1] = Buckets - 1;
        int end = 0;
        for (byte i = 0; i < Buckets - 2; i++)
        {
            var b = Request([0x30, 0x04, i]);
            if (b[16] == 0) continue; // empty
            int start = b[17] | b[18] << 8, slots = b[19] | b[20] << 8;
            end = Math.Max(end, start + slots);
        }
        _start[0] = end;
        _start[1] = end + Slots;
        _ready[0] = _ready[1] = false;
        _shown = -1;
    }

    /// <summary>Clears every other bucket and starts ours at slot 0. For when the planned slots do not fit.</summary>
    void Replan()
    {
        ShowFirmwareScreen(); // a bucket on screen cannot be deleted
        for (byte i = 0; i < Buckets; i++) Request([0x32, 0x02, i]); // RAM only; "did not exist" is fine
        _start[0] = 0;
        _start[1] = Slots;
        _ready[0] = _ready[1] = false;
        _shown = -1;
    }

    public void SetBrightness(int percent)
    {
        byte b = (byte)Math.Clamp(percent, 0, 100);
        Send([0x30, 0x02, 0x01, b, 0x00, 0x00, 0x00, 0x00]); // only the backlight field is flagged
        BrightnessOnDevice = b;
    }

    public void ShowFirmwareScreen()
    {
        Ok(Request([0x38, 0x01, OutputFirmware, 0x00]));
        _shown = -1;
    }

    /// <summary>Liquid temperature (°C) and pump speed (rpm) from the cooler's status report, or NaN.</summary>
    public (float Liquid, float Pump) ReadStatus()
    {
        try
        {
            var r = Request([0x74, 0x01], 800);
            float liquid = r[15] + r[16] / 10f, pump = r[17] | r[18] << 8;
            return (liquid is > 0 and < 100 ? liquid : float.NaN, pump);
        }
        catch (IOException) { return (float.NaN, float.NaN); }
        catch (TimeoutException) { return (float.NaN, float.NaN); }
    }

    // ------------------------------------------------------------------ frames

    public static byte[] Header(int length)
    {
        var h = new byte[20];
        s_magic.CopyTo(h, 0);
        h[12] = AssetRaw;
        BitConverter.TryWriteBytes(h.AsSpan(16), (uint)length);
        return h;
    }

    /// <summary>Writes a frame to the bucket that is not on screen, then shows it.</summary>
    public void Upload(byte[] rgba)
    {
        if (rgba.Length != FrameBytes) throw new ArgumentException($"a frame must be {FrameBytes} bytes");
        int target = _shown == 0 ? 1 : 0;
        try { Fill(target, rgba); }
        catch (KrakenResultException)
        {
            // Start the bucket over once: cancel, delete and set it up again.
            try { Request([0x36, 0x03]); } catch (IOException) { }
            _ready[target] = false;
            Fill(target, rgba);
        }
        Ok(Request([0x38, 0x01, OutputBucket, _bucket[target]]));
        _shown = target;
    }

    void Fill(int target, byte[] rgba)
    {
        byte idx = _bucket[target];
        if (!_ready[target])
        {
            Request([0x32, 0x02, idx]); // delete; "did not exist" is expected the first time
            var setup = SetupBucket(target);
            if (setup[14] is 5 or 6)
            {
                Replan();
                setup = SetupBucket(target);
            }
            Ok(setup);
            _ready[target] = true;
        }
        Ok(Request([0x36, 0x01, idx]));
        _link.Bulk(Header(rgba.Length));
        _link.Bulk(rgba);
        Ok(Request([0x36, 0x02]));
    }

    byte[] SetupBucket(int target)
    {
        int start = _start[target];
        return Request([0x32, 0x01, _bucket[target], (byte)(_bucket[target] + 1), (byte)start, (byte)(start >> 8), unchecked((byte)Slots), Slots >> 8, 0x01]);
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>The real cooler: HID interface 1 (usage page FF00) for commands, WinUSB interface 0 for pixels.</summary>
internal sealed class KrakenUsb : IKrakenLink
{
    public const int Vendor = 0x1E71, Product = 0x3008;
    static readonly Guid s_winUsb = new("30082008-7EE7-1125-0724-101503010819");

    readonly HidStream _hid;
    readonly WinUsbPipe _bulk;

    KrakenUsb(HidStream hid, WinUsbPipe bulk) { _hid = hid; _bulk = bulk; }

    public static HidDevice? Find()
    {
        foreach (var hid in DeviceList.Local.GetHidDevices(Vendor, Product))
        {
            try { if (hid.GetMaxOutputReportLength() == 64 && hid.GetMaxInputReportLength() == 64) return hid; }
            catch { }
        }
        return null;
    }

    /// <summary>Opens both interfaces. Null when the cooler is not connected.</summary>
    public static KrakenUsb? Open()
    {
        var device = Find();
        if (device == null) return null;
        if (!device.TryOpen(out var hid)) throw new IOException("the cooler's control interface is in use by another program");
        WinUsbPipe? bulk = null;
        try
        {
            bulk = WinUsbPipe.Open(s_winUsb, 3000) ?? throw new IOException("the cooler's image interface was not found. Its USB driver comes with NZXT CAM: install CAM once, then close it");
            return new KrakenUsb(hid, bulk);
        }
        catch
        {
            hid.Dispose();
            bulk?.Dispose();
            throw;
        }
    }

    public void Write(byte[] report) => _hid.Write(report);

    public byte[]? Read(int timeoutMs)
    {
        _hid.ReadTimeout = Math.Max(1, timeoutMs);
        var buffer = new byte[64];
        try { return _hid.Read(buffer, 0, 64) >= 15 ? buffer : null; }
        catch (TimeoutException) { return null; }
    }

    public void Bulk(ReadOnlySpan<byte> data) => _bulk.Write(data);

    public void Dispose()
    {
        _bulk.Dispose();
        _hid.Dispose();
    }
}

/// <summary>
/// Gauge's side of the Kraken's screen: the settings, a live preview, and a background loop that samples the
/// chosen reading once a second and uploads a new frame when the shown number changes. Stands aside while
/// NZXT CAM runs (only one program can hold the cooler) and gives the screen back on exit.
/// </summary>
internal sealed class KrakenLcd : IDisposable
{
    public string Name = "NZXT Kraken Z3";
    public readonly object Sync = new();
    /// <summary>Reads CPU and GPU values; set by the window. Liquid temperature comes from the cooler.</summary>
    public Func<LcdReading, float>? ReadSensor;
    public event Action? Changed;

    LcdSettings _settings = LcdSettings.Load();
    int _version;
    Bitmap? _preview;
    string _status = "Starting";
    bool _problem;
    float _liquid = float.NaN, _pump = float.NaN;

    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    Thread? _thread;

    public LcdSettings Settings { get { lock (Sync) return _settings.Clone(); } }
    public string Status { get { lock (Sync) return _status; } }
    public bool Problem { get { lock (Sync) return _problem; } }
    public float Liquid { get { lock (Sync) return _liquid; } }
    public float Pump { get { lock (Sync) return _pump; } }

    /// <summary>The latest frame, upright, for the window's preview. Caller holds <see cref="Sync"/>.</summary>
    public Bitmap? Preview => _preview;

    /// <summary>True when the screen shows a CPU or GPU value, which needs the monitor sampling even while Gauge is hidden.</summary>
    public bool NeedsTelemetry { get { lock (Sync) return _settings.Drive && _settings.Reading != LcdReading.LiquidTemp; } }

    public void Start()
    {
        _thread ??= new Thread(Run) { IsBackground = true, Name = "KrakenLcd" };
        if (!_thread.IsAlive) _thread.Start();
    }

    public void Change(Action<LcdSettings> change)
    {
        lock (Sync)
        {
            change(_settings);
            _settings.Save();
            _version++;
        }
        _wake.Set();
    }

    void SetStatus(string status, bool problem)
    {
        bool changed;
        lock (Sync) { changed = status != _status || problem != _problem; _status = status; _problem = problem; }
        if (changed) { Log.Write($"kraken: {status}"); Changed?.Invoke(); }
    }

    static bool CamRunning()
    {
        foreach (string name in (ReadOnlySpan<string>)["NZXT CAM", "cam_helper"])
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    float Value(LcdSettings s, float liquid)
    {
        if (s.Reading == LcdReading.LiquidTemp) return liquid;
        try { return ReadSensor?.Invoke(s.Reading) ?? float.NaN; } catch { return float.NaN; }
    }

    void Run()
    {
        KrakenSession? session = null;
        int failures = 0, failedVersion = -1, lastBrightness = -1;
        string? lastKey = null, lastError = null;

        void Close(bool giveBack)
        {
            if (session == null) return;
            if (giveBack) try { session.ShowFirmwareScreen(); } catch { }
            session.Dispose();
            session = null;
            lastKey = null;
            lastBrightness = -1;
        }

        while (!_stop)
        {
            LcdSettings s;
            int version;
            lock (Sync) { s = _settings.Clone(); version = _version; }
            int wait = 1000;
            try
            {
                if (!s.Drive)
                {
                    // Give the screen back (once, as the session closes), then leave the cooler alone.
                    Close(giveBack: true);
                    SetStatus("The Kraken is showing its own screen", false);
                    UpdatePreview(s, version, float.NaN, ref lastKey, upload: null);
                }
                else if (CamRunning())
                {
                    Close(giveBack: false);
                    SetStatus("NZXT CAM is running. Close it to let Gauge drive the screen", true);
                    UpdatePreview(s, version, float.NaN, ref lastKey, upload: null);
                    wait = 3000;
                }
                else if (failures >= 3 && version == failedVersion)
                {
                    SetStatus($"Stopped after repeated errors ({lastError}). Change a setting to try again", true);
                }
                else
                {
                    if (session == null)
                    {
                        var link = KrakenUsb.Open();
                        if (link == null)
                        {
                            SetStatus("The cooler is not connected", true);
                            wait = 3000;
                            goto Sleep;
                        }
                        session = new KrakenSession(link);
                        session.Init();
                        lastKey = null;
                    }
                    var (liquid, pump) = session.ReadStatus();
                    lock (Sync) { _liquid = liquid; _pump = pump; }
                    if (s.Brightness != lastBrightness) { session.SetBrightness(s.Brightness); lastBrightness = s.Brightness; }
                    UpdatePreview(s, version, Value(s, liquid), ref lastKey, upload: session);
                    failures = 0;
                    SetStatus("Gauge is drawing the screen", false);
                }
            }
            catch (Exception ex)
            {
                failures = version == failedVersion ? failures + 1 : 1;
                failedVersion = version;
                lastError = ex.Message;
                Log.Write($"kraken: {ex.GetType().Name}: {ex.Message}");
                Close(giveBack: false);
                SetStatus($"Could not drive the screen: {ex.Message}", true);
                wait = 1000 << Math.Min(failures - 1, 2); // 1, 2, 4 s, as CAM does
            }
        Sleep:
            _wake.WaitOne(wait);
        }
        Close(giveBack: true);
    }

    /// <summary>Renders when the shown number or a setting changed, keeps it as the preview, and uploads it when driving.</summary>
    void UpdatePreview(LcdSettings s, int version, float value, ref string? lastKey, KrakenSession? upload)
    {
        string key = $"{version}|{(float.IsNaN(value) ? "-" : MathF.Round(value).ToString())}|{upload != null}";
        if (key == lastKey) return;
        var frame = KrakenFrame.Render(s, value);
        if (upload != null) upload.Upload(KrakenFrame.ToRgba(frame, s.Rotation));
        lock (Sync)
        {
            _preview?.Dispose();
            _preview = frame;
        }
        lastKey = key;
        Changed?.Invoke();
    }

    /// <summary>Snapshots only: shows a given state without a cooler, a thread or saving settings.</summary>
    internal void ShowForSnapshot(LcdSettings settings, float value, string status, bool problem, float liquid, float pump)
    {
        lock (Sync)
        {
            _settings = settings;
            _preview?.Dispose();
            _preview = KrakenFrame.Render(settings, value);
            _status = status; _problem = problem; _liquid = liquid; _pump = pump;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(3000); // long enough to hand the screen back
        lock (Sync) { _preview?.Dispose(); _preview = null; }
    }
}
