using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;

namespace Gauge;

internal sealed class SpecSection(string title)
{
    public readonly string Title = title;
    public readonly List<(string Key, string Value)> Rows = new();
    /// <summary>Rows that also carry a 0..1 fill, drawn as a small meter.</summary>
    public readonly Dictionary<string, float> Meters = new();

    public void Add(string key, string? value)
    {
        value = value?.Trim();
        if (!string.IsNullOrEmpty(value)) Rows.Add((key, value));
    }
}

/// <summary>Headline numbers pulled out of the inventory for the figures strip.</summary>
internal sealed class SpecSummary
{
    public int Cores, Threads, BaseMhz, RamSpeed, Drives, ScreenW, ScreenH, ScreenHz;
    public long VramBytes, RamBytes, StorageBytes;
    public string GpuName = "", RamType = "";
}

/// <summary>One-off hardware inventory, read from the registry and WMI. Nothing here is typed in by the user.</summary>
internal static unsafe class Specs
{
    static SpecSummary s_sum = new();

    public static List<SpecSection> Collect(Telemetry t, out SpecSummary summary)
    {
        s_sum = new SpecSummary { Threads = Environment.ProcessorCount, BaseMhz = t.BaseMhz };
        var list = new List<SpecSection>();
        Try(list, System);
        Try(list, () => Processor(t));
        Try(list, () => Graphics(t));
        Try(list, Memory);
        Try(list, Storage);
        Try(list, Displays);
        Try(list, Network);
        summary = s_sum;
        return list;
    }

    static void Try(List<SpecSection> list, Func<SpecSection> build)
    {
        try { var s = build(); if (s.Rows.Count > 0) list.Add(s); } catch { }
    }

    static SpecSection System()
    {
        var s = new SpecSection("System");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Get(string name) => (bios?.GetValue(name) as string ?? "").Trim();

        string maker = Get("SystemManufacturer"), product = Get("SystemProductName"), family = Get("SystemFamily");
        bool generic = product.Length == 0 || product.Contains("System Product Name") || product.Contains("To Be Filled");
        if (!generic) s.Add("Device", family.Length > 0 && !family.Contains("To Be Filled") && family != product ? $"{maker} {family} ({product})" : $"{maker} {product}");
        s.Add("Motherboard", $"{Get("BaseBoardManufacturer")} {Get("BaseBoardProduct")}");
        s.Add("BIOS", $"{Get("BIOSVendor")} {Get("BIOSVersion")}" + (Get("BIOSReleaseDate") is { Length: > 0 } d ? $", {d}" : ""));

        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = nt?.GetValue("ProductName") as string ?? "Windows";
        string build = nt?.GetValue("CurrentBuild") as string ?? "";
        if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        s.Add("Operating system", $"{name} {nt?.GetValue("DisplayVersion")}");
        s.Add("Build", $"{build}.{nt?.GetValue("UBR")}");
        return s;
    }

    static SpecSection Processor(Telemetry t)
    {
        var s = new SpecSection("Processor");
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        s.Add("Model", string.Join(' ', (key?.GetValue("ProcessorNameString") as string ?? t.CpuName).Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        int cores = 0;
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(0, null, ref len);
        if (len > 0)
        {
            byte[] buf = new byte[len];
            fixed (byte* p = buf)
                if (Native.GetLogicalProcessorInformationEx(0, p, ref len))
                    for (uint off = 0; off < len; off += *(uint*)(p + off + 4)) cores++;
        }
        s_sum.Cores = cores;
        s.Add("Cores", cores > 0 ? $"{cores} cores, {Environment.ProcessorCount} threads" : $"{Environment.ProcessorCount} threads");
        if (t.BaseMhz > 0) s.Add("Base clock", $"{t.BaseMhz / 1000.0:0.0} GHz");
        return s;
    }

    static SpecSection Graphics(Telemetry t)
    {
        var s = new SpecSection("Graphics");
        var vram = new Dictionary<string, long>();
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using (var root = Registry.LocalMachine.OpenSubKey(cls))
        {
            foreach (string sub in root?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var k = root!.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string desc) continue;
                    long bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        byte[] { Length: >= 8 } a => BitConverter.ToInt64(a, 0),
                        _ => 0,
                    };
                    if (bytes > 0) vram[desc] = bytes;
                }
                catch { }
            }
        }

        int n = 0;
        using var q = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
        foreach (ManagementBaseObject mo in q.Get())
        {
            string name = mo["Name"] as string ?? "";
            if (name.Length == 0 || name.Contains("Basic Display") || name.Contains("Remote Display")) continue;
            n++;
            s.Add($"Adapter {n}", name);
            long bytes = vram.GetValueOrDefault(name);
            lock (t.Sync)
                foreach (var g in t.Gpus)
                    if (g.Name == name && g.VramTotal > 0) bytes = Math.Max(bytes, (long)g.VramTotal * 1024 * 1024);
            if (s_sum.GpuName.Length == 0 || bytes > s_sum.VramBytes) { s_sum.GpuName = name; s_sum.VramBytes = bytes; }
            if (bytes > 0) s.Add("  Memory", Format.Bytes(bytes, binary: true));
            s.Add("  Driver", mo["DriverVersion"] as string);
        }
        if (n == 1) s.Rows[0] = ("Adapter", s.Rows[0].Value);
        return s;
    }

    static SpecSection Memory()
    {
        var s = new SpecSection("Memory");
        long total = 0;
        int type = 0, n = 0;
        var modules = new List<string>();
        using var q = new ManagementObjectSearcher("SELECT Capacity, Manufacturer, PartNumber, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        foreach (ManagementBaseObject mo in q.Get())
        {
            long cap = Convert.ToInt64(mo["Capacity"] ?? 0L);
            int speed = Convert.ToInt32(mo["ConfiguredClockSpeed"] ?? mo["Speed"] ?? 0);
            type = Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0);
            total += cap;
            s_sum.RamSpeed = Math.Max(s_sum.RamSpeed, speed);
            string maker = (mo["Manufacturer"] as string ?? "").Trim(), part = (mo["PartNumber"] as string ?? "").Trim();
            modules.Add($"{Format.Bytes(cap, binary: true)}{(speed > 0 ? $" {speed} MHz" : "")}  {maker} {part}".TrimEnd());
        }
        string gen = type switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => "" };
        s_sum.RamBytes = total;
        s_sum.RamType = gen;
        if (total > 0) s.Add("Installed", $"{Format.Bytes(total, binary: true)} {gen}");
        foreach (string m in modules) s.Add($"Module {++n}", m);
        return s;
    }

    static SpecSection Storage()
    {
        var s = new SpecSection("Storage");
        int n = 0;
        using var q = new ManagementObjectSearcher("SELECT Model, Size, InterfaceType, Index FROM Win32_DiskDrive");
        var disks = new List<(int Index, string Text)>();
        foreach (ManagementBaseObject mo in q.Get())
        {
            long size = Convert.ToInt64(mo["Size"] ?? 0L);
            string model = (mo["Model"] as string ?? "Disk").Trim();
            s_sum.StorageBytes += size;
            s_sum.Drives++;
            disks.Add((Convert.ToInt32(mo["Index"] ?? 0), size > 0 ? $"{model}  ·  {Format.Bytes(size)}" : model));
        }
        disks.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var d in disks) s.Add(disks.Count == 1 ? "Drive" : $"Drive {++n}", d.Text);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string label = d.VolumeLabel.Length > 0 ? $"{d.VolumeLabel}, " : "";
                if (d.TotalSize > 0) s.Meters[$"Volume {d.Name.TrimEnd('\\', ':')}"] = 1f - (float)d.TotalFreeSpace / d.TotalSize;
                s.Add($"Volume {d.Name.TrimEnd('\\', ':')}",$"{label}{d.DriveFormat}, {Format.Bytes(d.TotalFreeSpace, binary: true)} free of {Format.Bytes(d.TotalSize, binary: true)}");
            }
            catch { }
        }
        return s;
    }

    static SpecSection Displays()
    {
        var s = new SpecSection("Display");
        var screens = Screen.AllScreens;
        byte* dm = stackalloc byte[220]; // DEVMODEW
        for (int i = 0; i < screens.Length; i++)
        {
            new Span<byte>(dm, 220).Clear();
            *(ushort*)(dm + 68) = 220;
            string text = Native.EnumDisplaySettingsW(screens[i].DeviceName, -1, dm)
                ? $"{*(uint*)(dm + 172)} × {*(uint*)(dm + 176)} at {*(uint*)(dm + 184)} Hz"
                : $"{screens[i].Bounds.Width} × {screens[i].Bounds.Height}";
            if (screens[i].Primary && *(uint*)(dm + 172) > 0) { s_sum.ScreenW = (int)*(uint*)(dm + 172); s_sum.ScreenH = (int)*(uint*)(dm + 176); s_sum.ScreenHz = (int)*(uint*)(dm + 184); }
            if (screens[i].Primary && screens.Length > 1) text += ", primary";
            s.Add(screens.Length == 1 ? "Screen" : $"Screen {i + 1}", text);
        }
        return s;
    }

    static SpecSection Network()
    {
        var s = new SpecSection("Network");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Telemetry.IsPhysicalAdapter(ni)) continue;
            bool up = ni.OperationalStatus == OperationalStatus.Up;
            string speed = up && ni.Speed > 0 ? ni.Speed >= 1_000_000_000 ? $"  ·  {ni.Speed / 1e9:0.#} Gbps" : $"  ·  {ni.Speed / 1e6:0} Mbps" : "";
            s.Add(ni.Name, ni.Description + (up ? speed : "  ·  not connected"));
        }
        return s;
    }

    public static string ToText(List<SpecSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var sec in sections)
        {
            sb.AppendLine(sec.Title);
            foreach (var (k, v) in sec.Rows) sb.Append("  ").Append(k.Trim()).Append(": ").AppendLine(v);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

internal static class Format
{
    /// <summary>Sizes. Disks use decimal units as printed on the box; memory uses binary.</summary>
    public static string Bytes(long bytes, bool binary = false)
    {
        double unit = binary ? 1024 : 1000;
        double gb = bytes / (unit * unit * unit);
        if (gb >= 1000) return $"{gb / unit:0.##} TB";
        if (gb >= 100 || binary && gb == Math.Floor(gb)) return $"{gb:0} GB";
        if (gb >= 1) return $"{gb:0.#} GB";
        return $"{bytes / (unit * unit):0} MB";
    }

    public static (string Value, string Unit) Rate(float bytesPerSecond)
    {
        if (bytesPerSecond < 1000) return (bytesPerSecond.ToString("0"), "B/s");
        if (bytesPerSecond < 1024 * 1000) return ((bytesPerSecond / 1024).ToString(bytesPerSecond < 10240 ? "0.0" : "0"), "KB/s");
        float mb = bytesPerSecond / (1024 * 1024);
        return (mb.ToString(mb < 100 ? "0.0" : "0"), "MB/s");
    }
}
