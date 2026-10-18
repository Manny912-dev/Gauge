using System.Diagnostics;
using HidSharp;

namespace Gauge;

/// <summary>What the Kraken's screen shows as its number.</summary>
internal enum LcdReading { LiquidTemp, CpuTemp, CpuLoad, CpuClock, GpuTemp, GpuLoad, GpuClock }

/// <summary>The screen's design, as NZXT CAM names its pages: one reading, two readings, or an image or GIF.</summary>
internal enum LcdMode { Single, Dual, Media }

/// <summary>The screen's settings, mirroring NZXT CAM's LCD pages. Stored as lcd.* in settings.ini.</summary>
internal sealed class LcdSettings
{
    /// <summary>False: the Kraken shows its own built-in screen and Gauge leaves the cooler alone.</summary>
    public bool Drive;
    public LcdMode Mode;
    /// <summary>False: the radial ring. True: the full-screen gradient ("Full Fill").</summary>
    public bool Fill;
    public LcdReading Reading = LcdReading.LiquidTemp;
    public Color Color1 = Color.FromArgb(255, 0x6B, 0x00, 0xDE), Color2 = Color.FromArgb(255, 0xD6, 0x00, 0xBF);
    public Color Background = Color.Black, Ink = Color.White;
    public bool Logo = true;
    /// <summary>0..100, sent without re-uploading the image.</summary>
    public int Brightness = 50;
    /// <summary>
    /// The "Rotate Display" dial: how far the pump head is turned clockwise, 0..330 in 30° steps. Frames are
    /// turned back by as much before upload (see <see cref="KrakenFrame.RawClockwise"/>), so 0 is upright.
    /// </summary>
    public int Dial;

    // Dual Infographic: reading 1 on the left arc, reading 2 on the right
    public LcdReading Reading1 = LcdReading.CpuTemp, Reading2 = LcdReading.GpuTemp;
    public Color Arc1 = Color.FromArgb(255, 0x6B, 0x00, 0xDE), Arc2 = Color.FromArgb(255, 0xD6, 0x00, 0xBF);
    public Color Number1 = Color.White, Number2 = Color.White, Label1 = Color.White, Label2 = Color.White;

    /// <summary>The library item shown in Image/GIF mode (see <see cref="KrakenMedia"/>), or "" for the Kraken's own liquid screen.</summary>
    public string Media = "";

    public LcdSettings Clone() => (LcdSettings)MemberwiseClone();

    public static LcdSettings Load()
    {
        var s = new LcdSettings();
        s.Drive = Gauge.Settings.Get("lcd.drive") == "1";
        if (Enum.TryParse<LcdMode>(Gauge.Settings.Get("lcd.mode"), out var mode)) s.Mode = mode;
        s.Fill = Gauge.Settings.Get("lcd.style") == "fill";
        if (Enum.TryParse<LcdReading>(Gauge.Settings.Get("lcd.reading"), out var r)) s.Reading = r;
        if (Gauge.Settings.ParseColor(Gauge.Settings.Get("lcd.color1")) is Color c1) s.Color1 = c1;
        if (Gauge.Settings.ParseColor(Gauge.Settings.Get("lcd.color2")) is Color c2) s.Color2 = c2;
        s.Logo = Gauge.Settings.Get("lcd.logo") != "0";
        if (int.TryParse(Gauge.Settings.Get("lcd.brightness"), out int b)) s.Brightness = Math.Clamp(b, 0, 100);
        if (int.TryParse(Gauge.Settings.Get("lcd.dial"), out int dial) && dial is >= 0 and < 360 && dial % 30 == 0) s.Dial = dial;
        else if (int.TryParse(Gauge.Settings.Get("lcd.rotation"), out int rot) && rot is 0 or 90 or 180 or 270)
            s.Dial = (360 - rot) % 360; // before 0.9 this turned frames clockwise by rot: the same picture
        if (Enum.TryParse<LcdReading>(Gauge.Settings.Get("lcd.dual.reading1"), out var r1)) s.Reading1 = r1;
        if (Enum.TryParse<LcdReading>(Gauge.Settings.Get("lcd.dual.reading2"), out var r2)) s.Reading2 = r2;
        Color Pick(string key, Color fallback) => Gauge.Settings.ParseColor(Gauge.Settings.Get(key)) ?? fallback;
        s.Arc1 = Pick("lcd.dual.arc1", s.Arc1);
        s.Arc2 = Pick("lcd.dual.arc2", s.Arc2);
        s.Number1 = Pick("lcd.dual.number1", s.Number1);
        s.Number2 = Pick("lcd.dual.number2", s.Number2);
        s.Label1 = Pick("lcd.dual.label1", s.Label1);
        s.Label2 = Pick("lcd.dual.label2", s.Label2);
        s.Media = Gauge.Settings.Get("lcd.media") ?? "";
        return s;
    }

    public void Save() => Gauge.Settings.Set(
        ("lcd.drive", Drive ? "1" : "0"), ("lcd.mode", Mode.ToString()), ("lcd.style", Fill ? "fill" : "ring"), ("lcd.reading", Reading.ToString()),
        ("lcd.color1", Gauge.Settings.Hex(Color1)), ("lcd.color2", Gauge.Settings.Hex(Color2)), ("lcd.logo", Logo ? "1" : "0"),
        ("lcd.brightness", Brightness.ToString()), ("lcd.dial", Dial.ToString()), ("lcd.rotation", null),
        ("lcd.dual.reading1", Reading1.ToString()), ("lcd.dual.reading2", Reading2.ToString()),
        ("lcd.dual.arc1", Gauge.Settings.Hex(Arc1)), ("lcd.dual.arc2", Gauge.Settings.Hex(Arc2)),
        ("lcd.dual.number1", Gauge.Settings.Hex(Number1)), ("lcd.dual.number2", Gauge.Settings.Hex(Number2)),
        ("lcd.dual.label1", Gauge.Settings.Hex(Label1)), ("lcd.dual.label2", Gauge.Settings.Hex(Label2)),
        ("lcd.media", Media.Length > 0 ? Media : null));
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
/// What Gauge knows of the cooler's RAM: 16 buckets over 24,320 slots of 1 KiB, each holding one contiguous
/// range. Pure bookkeeping, apart from the USB traffic, so its rules can be tested on their own. The cooler's
/// own answers stay the authority: a refusal makes the session read every bucket again.
/// </summary>
internal sealed class KrakenMemory
{
    public const int Buckets = 16, TotalSlots = 24_320, SlotBytes = 1024;

    public readonly record struct Bucket(int Start, int Slots, byte Type);

    readonly Bucket?[] _buckets = new Bucket?[Buckets];
    readonly long[] _used = new long[Buckets];
    long _clock;

    /// <summary>The bucket on screen, or -1 for the Kraken's own screen (or not known).</summary>
    public int Shown = -1;

    /// <summary>
    /// Slots Gauge believes the cooler has. Starts at <see cref="TotalSlots"/>, which is inferred from NZXT CAM's size
    /// limit; lowered when the cooler calls a range beyond it invalid (result 6).
    /// </summary>
    public int Capacity = TotalSlots;

    public static int SlotsFor(long bytes) => (int)((bytes + SlotBytes - 1) / SlotBytes);

    public Bucket? this[int index] => _buckets[index];

    /// <summary>Records a bucket Gauge just set up; it counts as the most recently used.</summary>
    public void Set(int index, Bucket bucket) { _buckets[index] = bucket; Touch(index); }

    /// <summary>Records a bucket found on the cooler, which counts as older than anything Gauge used.</summary>
    public void Found(int index, Bucket bucket) { _buckets[index] = bucket; _used[index] = 0; }

    public void Clear(int index) => _buckets[index] = null;

    public void Touch(int index) => _used[index] = ++_clock;

    /// <summary>The lowest start of a free run of <paramref name="slots"/> slots, not counting bucket <paramref name="replacing"/>, or -1.</summary>
    public int FindRange(int slots, int replacing)
    {
        if (slots <= 0 || slots > Capacity) return -1;
        var taken = new List<Bucket>();
        for (int i = 0; i < Buckets; i++) if (i != replacing && _buckets[i] is { } b) taken.Add(b);
        taken.Sort((a, b) => a.Start.CompareTo(b.Start));
        int start = 0;
        foreach (var b in taken)
        {
            if (b.Start - start >= slots) return start;
            start = Math.Max(start, b.Start + b.Slots);
        }
        return Capacity - start >= slots ? start : -1;
    }

    /// <summary>
    /// The bucket to delete next to make room: the least recently used one that is neither on screen nor
    /// <paramref name="replacing"/>. Buckets in <paramref name="keep"/> go only once nothing else is left. -1 when none can go.
    /// </summary>
    public int Victim(int replacing, ReadOnlySpan<int> keep)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int best = -1;
            for (int i = 0; i < Buckets; i++)
            {
                if (i == replacing || i == Shown || _buckets[i] == null || (pass == 0 && keep.Contains(i))) continue;
                if (best < 0 || _used[i] < _used[best]) best = i;
            }
            if (best >= 0) return best;
        }
        return -1;
    }
}

/// <summary>
/// The Kraken Z3 LCD protocol over an <see cref="IKrakenLink"/>: commands are <c>[cmd, sub, …]</c> padded to
/// 64 bytes, replies come back as <c>[cmd+1, sub, …]</c> with the result at byte 14 (1 = success), mixed in
/// with the cooler's own status reports. Live frames are 320×320 RGBA written to two RAM buckets in turn, so
/// the one on screen is never the one being written; an image or GIF goes once into a third bucket and the
/// cooler plays it by itself. Room is shared through <see cref="KrakenMemory"/>. Never touches flash.
/// </summary>
internal sealed class KrakenSession : IDisposable
{
    public const int Size = 320, FrameBytes = Size * Size * 4, Slots = (FrameBytes + 1023) / 1024; // 400 slots of 1 KiB
    public const byte AssetGif = 0x01, AssetRaw = 0x02;
    /// <summary>Gauge's buckets: two for live frames in turn, one for the current image or GIF.</summary>
    public const int RawA = 14, RawB = 15, GifBucket = 13;
    /// <summary>The largest GIF the cooler holds: every slot.</summary>
    public const int MaxGifBytes = KrakenMemory.TotalSlots * KrakenMemory.SlotBytes;
    static readonly byte[] s_magic = [0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10];
    const byte OutputFirmware = 0x02, OutputBucket = 0x04;
    // Whole USB packets per write, so a split upload looks the same on the wire as one long write
    const int ChunkBytes = 256 * 1024;

    readonly IKrakenLink _link;
    public readonly KrakenMemory Memory = new();
    readonly bool[] _ready = new bool[KrakenMemory.Buckets]; // set up for its current content, so the next one can go straight in
    public int BrightnessOnDevice { get; private set; } = -1;

    /// <summary>What the GIF bucket holds: the key of the image Gauge put there, or null.</summary>
    public string? GifKey { get; private set; }

    /// <summary>The GIF bucket's key and place, for recognising it after a restart; null when it holds nothing of Gauge's.</summary>
    public string? GifPlacement => GifKey != null && Memory[GifBucket] is { } b ? $"{GifKey}|{b.Start}|{b.Slots}" : null;

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

    void Cancel()
    {
        try { Request([0x36, 0x03]); } catch (IOException) { } catch (TimeoutException) { }
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Cancels any half-finished transfer, reads the screen's state and every bucket. <paramref name="rememberedGif"/>
    /// is an earlier <see cref="GifPlacement"/>: when the GIF bucket is still exactly there, its image is not sent again.
    /// </summary>
    public void Init(string? rememberedGif = null)
    {
        Cancel(); // CAM does this too; the result does not matter
        var info = Request([0x30, 0x01]);
        BrightnessOnDevice = info[24];
        Rescan();
        Memory.Shown = -1;
        int bar = rememberedGif?.LastIndexOf('|') ?? -1, bar2 = bar > 0 ? rememberedGif!.LastIndexOf('|', bar - 1) : -1;
        if (bar2 > 0 && Memory[GifBucket] is { } b
            && int.TryParse(rememberedGif.AsSpan(bar2 + 1, bar - bar2 - 1), out int start) && int.TryParse(rememberedGif.AsSpan(bar + 1), out int slots)
            && b.Start == start && b.Slots == slots)
            GifKey = rememberedGif[..bar2];
    }

    /// <summary>Reads every bucket from the cooler, replacing what Gauge believed.</summary>
    void Rescan()
    {
        for (byte i = 0; i < KrakenMemory.Buckets; i++)
        {
            var r = Request([0x30, 0x04, i]);
            var old = Memory[i];
            if (r[16] == 0) Memory.Clear(i); // empty
            else
            {
                var found = new KrakenMemory.Bucket(r[17] | r[18] << 8, r[19] | r[20] << 8, r[21]);
                if (old != found) Memory.Found(i, found);
            }
            if (Memory[i] != old) _ready[i] = false;
        }
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
        Memory.Shown = -1;
    }

    void Show(int bucket)
    {
        Ok(Request([0x38, 0x01, OutputBucket, (byte)bucket]));
        Memory.Shown = bucket;
        Memory.Touch(bucket);
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

    // ------------------------------------------------------------------ buckets

    /// <summary>Deletes a bucket. One on screen cannot go (result 9), so the Kraken's own screen is shown first.</summary>
    void Delete(int bucket)
    {
        var r = Request([0x32, 0x02, (byte)bucket]);
        if (r[14] == 9)
        {
            ShowFirmwareScreen();
            r = Request([0x32, 0x02, (byte)bucket]);
        }
        if (r[14] is not (1 or 8)) throw new KrakenResultException(r[14]); // 8: it did not exist
        Memory.Clear(bucket);
        _ready[bucket] = false;
    }

    /// <summary>A free run of slots for <paramref name="bucket"/>, deleting the least recently used other buckets until one fits.</summary>
    int Place(int bucket, int slots, ReadOnlySpan<int> keep)
    {
        while (true)
        {
            int start = Memory.FindRange(slots, bucket);
            if (start >= 0) return start;
            int victim = Memory.Victim(bucket, keep);
            if (victim >= 0) { Delete(victim); continue; }
            if (Memory.Shown >= 0 && Memory.Shown != bucket) { ShowFirmwareScreen(); continue; } // only the bucket on screen is left
            throw new IOException("the cooler has no room for this image");
        }
    }

    /// <summary>Makes <paramref name="bucket"/> a bucket of <paramref name="slots"/> slots for <paramref name="type"/>, unless it already is one.</summary>
    void Prepare(int bucket, byte type, int slots, ReadOnlySpan<int> keep)
    {
        if (_ready[bucket] && Memory[bucket] is { } b && b.Slots == slots && b.Type == type) return;
        if (Memory.Shown == bucket) ShowFirmwareScreen();
        Delete(bucket);
        for (int attempt = 0; ; attempt++)
        {
            int start = Place(bucket, slots, keep);
            var r = Request([0x32, 0x01, (byte)bucket, (byte)(bucket + 1), (byte)start, (byte)(start >> 8), (byte)slots, (byte)(slots >> 8), 0x01]);
            if (r[14] == 1)
            {
                Memory.Set(bucket, new KrakenMemory.Bucket(start, slots, type));
                _ready[bucket] = true;
                return;
            }
            if (r[14] is 5 or 6 && attempt < 8)
            {
                // Overlap or invalid slots: the cooler knows better than Gauge's notes. Read them again first;
                // if the range is still invalid, the cooler holds fewer slots than assumed, so stay below it.
                if (r[14] == 6 && attempt > 0 && start + slots - 1 < Memory.Capacity)
                {
                    Memory.Capacity = start + slots - 1;
                    Log.Write($"kraken: the cooler refused slots {start}..{start + slots}; assuming at most {Memory.Capacity} slots");
                }
                else Rescan();
                Delete(bucket);
                continue;
            }
            throw new KrakenResultException(r[14]);
        }
    }

    public static byte[] Header(int length, byte type = AssetRaw)
    {
        var h = new byte[20];
        s_magic.CopyTo(h, 0);
        h[12] = type;
        BitConverter.TryWriteBytes(h.AsSpan(16), (uint)length);
        return h;
    }

    /// <summary>Writes one asset into a bucket: set up (when needed), start, header, payload, complete.</summary>
    void Write(int bucket, byte type, ReadOnlySpan<byte> payload, ReadOnlySpan<int> keep, Action<float>? progress)
    {
        Prepare(bucket, type, KrakenMemory.SlotsFor(payload.Length), keep);
        Ok(Request([0x36, 0x01, (byte)bucket]));
        _link.Bulk(Header(payload.Length, type));
        if (progress == null) _link.Bulk(payload);
        else
        {
            for (int done = 0; done < payload.Length; done += ChunkBytes)
            {
                _link.Bulk(payload.Slice(done, Math.Min(ChunkBytes, payload.Length - done)));
                progress(Math.Min(1, (done + ChunkBytes) / (float)payload.Length));
            }
        }
        Ok(Request([0x36, 0x02], payload.Length > FrameBytes ? 5000 : 1500));
        Memory.Touch(bucket);
    }

    /// <summary>Writes a live frame to the bucket that is not on screen, then shows it.</summary>
    public void Upload(byte[] rgba)
    {
        if (rgba.Length != FrameBytes) throw new ArgumentException($"a frame must be {FrameBytes} bytes");
        int target = Memory.Shown == RawA ? RawB : RawA;
        ReadOnlySpan<int> keep = [GifBucket]; // the GIF stays on the cooler while there is room, ready for the next switch
        try { Write(target, AssetRaw, rgba, keep, null); }
        catch (KrakenResultException)
        {
            // Start the bucket over once: cancel, delete and set it up again.
            Cancel();
            _ready[target] = false;
            Write(target, AssetRaw, rgba, keep, null);
        }
        Show(target);
    }

    /// <summary>
    /// Shows a processed 320×320 GIF, which the cooler then plays by itself. It is sent only when the GIF bucket
    /// does not already hold <paramref name="key"/>. Returns true when it was sent.
    /// </summary>
    public bool ShowGif(byte[] gif, string key, Action<float>? progress = null)
    {
        if (gif.Length > MaxGifBytes) throw new ArgumentException($"a GIF can be at most {MaxGifBytes} bytes");
        if (GifKey == key && Memory[GifBucket] != null)
        {
            Show(GifBucket);
            return false;
        }
        GifKey = null;
        ReadOnlySpan<int> keep = [RawA, RawB]; // live-frame buckets go only for a GIF that needs nearly all the room
        try { Write(GifBucket, AssetGif, gif, keep, progress); }
        catch (KrakenResultException)
        {
            Cancel();
            _ready[GifBucket] = false;
            Write(GifBucket, AssetGif, gif, keep, progress);
        }
        GifKey = key;
        Show(GifBucket);
        return true;
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
/// Gauge's side of the Kraken's screen: the settings, a live preview, and a background loop. In Single and Dual
/// it samples the readings once a second and uploads a new frame when a shown number changes; in Image/GIF it
/// sends the chosen picture once and the cooler plays it by itself. Stands aside while NZXT CAM runs (only one
/// program can hold the cooler) and gives the screen back on exit.
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
    float _progress = float.NaN;
    string? _mediaNote;
    bool _mediaBusy, _mediaNoteIsProblem;

    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    Thread? _thread;

    public LcdSettings Settings { get { lock (Sync) return _settings.Clone(); } }
    public string Status { get { lock (Sync) return _status; } }
    public bool Problem { get { lock (Sync) return _problem; } }
    public float Liquid { get { lock (Sync) return _liquid; } }
    public float Pump { get { lock (Sync) return _pump; } }
    /// <summary>How far a picture upload has got, 0..1, or NaN when none is running.</summary>
    public float Progress { get { lock (Sync) return _progress; } }
    /// <summary>The library's latest word: a picture being added, or why one was refused. Null when there is nothing to say.</summary>
    public string? MediaNote { get { lock (Sync) return _mediaNote; } }
    public bool MediaNoteIsProblem { get { lock (Sync) return _mediaNoteIsProblem; } }
    public bool MediaBusy { get { lock (Sync) return _mediaBusy; } }

    /// <summary>The latest frame, upright, for the window's preview. Caller holds <see cref="Sync"/>.</summary>
    public Bitmap? Preview => _preview;

    /// <summary>True when the screen shows a CPU or GPU value, which needs the monitor sampling even while Gauge is hidden.</summary>
    public bool NeedsTelemetry
    {
        get
        {
            lock (Sync)
                return _settings.Drive && _settings.Mode switch
                {
                    LcdMode.Single => _settings.Reading != LcdReading.LiquidTemp,
                    LcdMode.Dual => _settings.Reading1 != LcdReading.LiquidTemp || _settings.Reading2 != LcdReading.LiquidTemp,
                    _ => false,
                };
        }
    }

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

    void SetMediaNote(string? note, bool problem, bool busy)
    {
        lock (Sync) { _mediaNote = note; _mediaNoteIsProblem = problem; _mediaBusy = busy; }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the library, from the window

    /// <summary>Adds a picture with the chosen square on a background thread, then shows it. Problems end up in <see cref="MediaNote"/>.</summary>
    public void Import(string path, Rectangle crop)
    {
        int gifTurn;
        lock (Sync) gifTurn = KrakenFrame.GifClockwise(_settings.Dial);
        SetMediaNote($"Preparing {Path.GetFileName(path)}…", false, true);
        new Thread(() =>
        {
            try
            {
                var item = KrakenMedia.Import(path, crop, gifTurn);
                SetMediaNote(null, false, false);
                Change(s => { s.Media = item.Id; s.Mode = LcdMode.Media; });
            }
            catch (KrakenMediaException ex) { SetMediaNote(ex.Message, true, false); }
            catch (Exception ex)
            {
                Log.Write($"lcd media: import failed: {ex}");
                SetMediaNote($"Could not add the picture: {ex.Message}", true, false);
            }
        }) { IsBackground = true, Name = "KrakenMedia" }.Start();
    }

    public void DeleteMedia(string id)
    {
        KrakenMedia.Delete(id);
        bool shown;
        lock (Sync) shown = _settings.Media == id;
        if (shown) Change(s => s.Media = "");
        else Changed?.Invoke();
    }

    public void ShowMediaError(string message) => SetMediaNote(message, true, false);

    // ------------------------------------------------------------------ the loop

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

    float Value(LcdReading reading, float liquid)
    {
        if (reading == LcdReading.LiquidTemp) return liquid;
        try { return ReadSensor?.Invoke(reading) ?? float.NaN; } catch { return float.NaN; }
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
                    Show(s, version, float.NaN, ref lastKey, upload: null);
                }
                else if (CamRunning())
                {
                    Close(giveBack: false);
                    SetStatus("NZXT CAM is running. Close it to let Gauge drive the screen", true);
                    Show(s, version, float.NaN, ref lastKey, upload: null);
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
                        session.Init(Gauge.Settings.Get("lcd.gifcache"));
                        lastKey = null;
                    }
                    var (liquid, pump) = session.ReadStatus();
                    lock (Sync) { _liquid = liquid; _pump = pump; }
                    if (s.Brightness != lastBrightness) { session.SetBrightness(s.Brightness); lastBrightness = s.Brightness; }
                    Show(s, version, liquid, ref lastKey, upload: session);
                    failures = 0;
                }
            }
            catch (Exception ex)
            {
                failures = version == failedVersion ? failures + 1 : 1;
                failedVersion = version;
                lastError = ex.Message;
                Log.Write($"kraken: {ex.GetType().Name}: {ex.Message}");
                lock (Sync) _progress = float.NaN;
                Close(giveBack: false);
                SetStatus($"Could not drive the screen: {ex.Message}", true);
                wait = 1000 << Math.Min(failures - 1, 2); // 1, 2, 4 s, as CAM does
            }
        Sleep:
            _wake.WaitOne(wait);
        }
        Close(giveBack: true);
    }

    /// <summary>Brings the preview, and the cooler when <paramref name="upload"/> is given, up to date with the settings and readings.</summary>
    void Show(LcdSettings s, int version, float liquid, ref string? lastKey, KrakenSession? upload)
    {
        if (s.Mode == LcdMode.Media) ShowMedia(s, ref lastKey, upload);
        else
        {
            UpdatePreview(s, version, liquid, ref lastKey, upload);
            if (upload != null) SetStatus("Gauge is drawing the screen", false);
        }
    }

    /// <summary>Single and Dual: renders when a shown number or a setting changed, keeps it as the preview, and uploads it when driving.</summary>
    void UpdatePreview(LcdSettings s, int version, float liquid, ref string? lastKey, KrakenSession? upload)
    {
        static string Shown(float v) => float.IsNaN(v) ? "-" : MathF.Round(v).ToString();
        float v1 = Value(s.Mode == LcdMode.Dual ? s.Reading1 : s.Reading, liquid), v2 = s.Mode == LcdMode.Dual ? Value(s.Reading2, liquid) : float.NaN;
        string key = $"{version}|{Shown(v1)}|{Shown(v2)}|{upload != null}";
        if (key == lastKey) return;
        var frame = s.Mode == LcdMode.Dual ? KrakenFrame.RenderDual(s, v1, v2) : KrakenFrame.Render(s, v1);
        if (upload != null) upload.Upload(KrakenFrame.ToRgba(frame, s.Dial));
        SetPreview(frame);
        lastKey = key;
    }

    /// <summary>Image/GIF: sends the chosen picture once (the cooler animates it by itself), or shows the Kraken's liquid screen when none is chosen.</summary>
    void ShowMedia(LcdSettings s, ref string? lastKey, KrakenSession? upload)
    {
        var item = KrakenMedia.Find(s.Media);
        int gifTurn = KrakenFrame.GifClockwise(s.Dial);
        string key = (item == null ? "none" : KrakenMedia.Key(item, gifTurn)) + $"|{upload != null}";
        if (key == lastKey) return;
        lastKey = key;
        SetPreview(item == null ? null : KrakenMedia.Preview(item));
        if (upload == null) return;
        if (item == null)
        {
            upload.ShowFirmwareScreen();
            SetStatus("No picture chosen, so the Kraken shows its liquid temperature", false);
            return;
        }
        byte[] gif;
        try
        {
            SetStatus("Preparing the picture for the cooler", false);
            gif = KrakenMedia.Gif(item, gifTurn);
        }
        catch (KrakenMediaException ex)
        {
            upload.ShowFirmwareScreen();
            SetStatus(ex.Message, true);
            return;
        }
        int shownPercent = -1;
        bool sent = upload.ShowGif(gif, KrakenMedia.Key(item, gifTurn), p =>
        {
            int percent = (int)(p * 100);
            lock (Sync) _progress = p;
            if (percent == shownPercent) return;
            shownPercent = percent;
            SetStatus($"Sending the picture to the cooler  ·  {percent}%", false);
        });
        lock (Sync) _progress = float.NaN;
        if (sent) Gauge.Settings.Set(("lcd.gifcache", upload.GifPlacement));
        SetStatus(item.Frames > 1 ? "The cooler is playing your GIF by itself" : "The cooler is showing your picture", false);
    }

    void SetPreview(Bitmap? frame)
    {
        lock (Sync)
        {
            _preview?.Dispose();
            _preview = frame;
        }
        Changed?.Invoke();
    }

    /// <summary>Snapshots only: shows a given state without a cooler, a thread or saving settings.</summary>
    internal void ShowForSnapshot(LcdSettings settings, float value, string status, bool problem, float liquid, float pump, float value2 = float.NaN)
    {
        lock (Sync)
        {
            _settings = settings;
            _preview?.Dispose();
            _preview = settings.Mode switch
            {
                LcdMode.Dual => KrakenFrame.RenderDual(settings, value, value2),
                LcdMode.Media => KrakenMedia.Find(settings.Media) is { } item ? KrakenMedia.Preview(item) : null,
                _ => KrakenFrame.Render(settings, value),
            };
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
