using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Gauge;

internal sealed class Ring(int capacity)
{
    readonly float[] _data = new float[capacity];
    int _head;

    public int Capacity => _data.Length;
    public int Count { get; private set; }

    public void Push(float v)
    {
        _data[_head] = v;
        _head = (_head + 1) % _data.Length;
        if (Count < _data.Length) Count++;
    }

    public void Clear() => Count = _head = 0;

    /// <summary>Index 0 is the oldest sample.</summary>
    public float At(int i) => _data[(_head - Count + i + _data.Length * 2) % _data.Length];

    public float Max()
    {
        float m = 0;
        for (int i = 0; i < Count; i++) m = MathF.Max(m, At(i));
        return m;
    }
}

internal sealed class GpuState
{
    public string Name = "";
    public float Load = float.NaN, Temp = float.NaN, Clock = float.NaN, Fan = float.NaN, Power = float.NaN;
    public float VramUsed = float.NaN, VramTotal = float.NaN; // MB
    public float ClockMax = 1, FanMax = 2000;
    public bool FanIsPercent;
    public readonly Ring History = new(Telemetry.HistoryLen);

    internal IHardware Hardware = null!;
    internal nint Nvml;
    internal ISensor? SLoad, STemp, SClock, SFan, SPower, SVramUsed, SVramTotal;
}

internal struct DriveState
{
    public string Name;
    public long Used, Total;
}

/// <summary>Samples every metric once a second on its own thread. Readers take <see cref="Sync"/>.</summary>
internal sealed unsafe class Telemetry : IDisposable
{
    public const int HistoryLen = 90;

    public readonly object Sync = new();
    public event Action? Updated;
    public volatile bool Paused;
    /// <summary>True once hardware detection has finished, whether or not it found anything.</summary>
    public volatile bool HardwareReady;

    public readonly bool Elevated;
    public readonly string CpuName;
    public readonly int BaseMhz;

    public float CpuLoad, CpuTemp = float.NaN, CpuClock = float.NaN, CpuFan = float.NaN, CpuPower = float.NaN;
    public float CpuClockMax = 1, CpuFanMax = 2000;
    /// <summary>Where <see cref="CpuTemp"/> came from this tick: a hardware sensor's name, "ACPI thermal zone", or "" when nothing reported.</summary>
    public string CpuTempSource = "";
    /// <summary>
    /// True when the only CPU temperature is the motherboard's ACPI zone and it has not moved for <see cref="FrozenAfter"/>
    /// samples. Many desktop boards report a fixed number there, so <see cref="CpuTemp"/> is then left empty rather than shown as live.
    /// </summary>
    public bool CpuTempFrozen;
    const int FrozenAfter = 20;
    /// <summary>True when the PawnIO driver is installed, so restarting as administrator gives the hardware library its CPU sensors.</summary>
    public bool DriverInstalled; // settable so --snapshot can show the prompt on a PC without the driver
    public readonly Ring CpuHistory = new(HistoryLen), MemHistory = new(HistoryLen), DownHistory = new(HistoryLen), UpHistory = new(HistoryLen);

    public readonly List<GpuState> Gpus = new();
    public int GpuIndex;

    public ulong MemUsed, MemTotal;
    public float MemLoad;

    public float NetDown, NetUp;
    public string NetName = "";

    public readonly List<DriveState> Drives = new();
    public readonly ProcessSampler Processes = new();
    public bool NetPerProcess => _netTrace?.Active == true;

    readonly ManualResetEventSlim _stop = new(false);
    readonly Dictionary<int, float> _gpuByPid = new();
    readonly List<(uint Index, string Name, long Total)> _nics = new();
    readonly List<ISensor> _cpuClocks = new(), _cpuTemps = new(), _fans = new();
    Thread? _thread;
    PdhQuery? _pdh;
    nint _cZone, _cPerf, _cGpuEngine;
    Computer? _computer;
    IHardware? _cpu, _board;
    ISensor? _sCpuPower;
    NetTrace? _netTrace;
    long _idle, _kernel, _user, _rx, _tx, _netTs;
    int _tick, _cpuProbes, _acpiSame;
    float _acpiLast = float.NaN;
    bool _nvml;

    public Telemetry()
    {
        using (var id = WindowsIdentity.GetCurrent())
            Elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        CpuName = ShortCpuName(key?.GetValue("ProcessorNameString") as string ?? "Processor");
        BaseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;
        CpuClockMax = MathF.Max(1, BaseMhz);
        // Where the hardware library itself looks for the driver
        using (var pawnIo = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
            DriverInstalled = pawnIo != null;
    }

    public static string ShortCpuName(string raw)
    {
        string s = raw.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace(" CPU", "");
        foreach (string cut in (ReadOnlySpan<string>)[" w/ ", " with ", " @ "])
        {
            int i = s.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i];
        }
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Telemetry" };
        _thread.Start();
    }

    /// <summary>Switches the GPU shown on the monitor page. Caller holds <see cref="Sync"/>.</summary>
    public void NextGpu()
    {
        if (Gpus.Count < 2) return;
        GpuIndex = (GpuIndex + 1) % Gpus.Count;
        Gpus[GpuIndex].History.Clear(); // it was not sampled while hidden
    }

    void Run()
    {
        _pdh = new PdhQuery();
        _cZone = _pdh.Add(@"\Thermal Zone Information(*)\Temperature");
        _cPerf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _cGpuEngine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        Native.GetSystemTimes(out _idle, out _kernel, out _user);

        if (Elevated)
        {
            try { var trace = new NetTrace(); if (trace.Start()) _netTrace = trace; else trace.Dispose(); }
            catch (Exception ex) { Log.Once("nettrace", ex); }
        }

        Tick();
        try { OpenHardware(); } catch (Exception ex) { Log.Once("hardware", ex); }
        HardwareReady = true;
        lock (Sync)
        {
            Log.Write($"elevated={Elevated} netTrace={NetPerProcess} cpu=\"{CpuName}\" cpuSensors={_cpu != null} fans={_fans.Count} adapters={_nics.Count}");
            foreach (var g in Gpus) Log.Write($"gpu \"{g.Name}\" via {(g.Nvml != 0 ? "nvml" : "lhm")} load={g.Load} temp={g.Temp} vram={g.VramTotal}");
        }

        while (!_stop.Wait(1000))
        {
            if (Paused) continue;
            Tick();
        }
    }

    void Tick()
    {
        lock (Sync)
        {
            try { Sample(); } catch (Exception ex) { Log.Once("sample", ex); }
        }
        Updated?.Invoke();
    }

    void OpenHardware()
    {
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
        computer.Open();
        lock (Sync)
        {
            _computer = computer;
            foreach (var hw in computer.Hardware)
            {
                UpdateTree(hw);
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu when _cpu == null:
                        _cpu = hw;
                        // Every temperature sensor, preferred ones first. Each tick takes the first that reports, so one
                        // sensor the driver cannot read (e.g. the package on some Intel chips) does not hide the others.
                        foreach (string name in (ReadOnlySpan<string>)["Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core Average"])
                            if (Find(hw, SensorType.Temperature, name) is { } s) _cpuTemps.Add(s);
                        foreach (var s in hw.Sensors)
                            if (s.SensorType == SensorType.Temperature && !_cpuTemps.Contains(s) && !s.Name.Contains("Distance")) _cpuTemps.Add(s);
                        _sCpuPower = Find(hw, SensorType.Power, "Package", "CPU Package");
                        foreach (var s in hw.Sensors)
                            if (s.SensorType == SensorType.Clock && s.Name.Contains("Core #") && !s.Name.Contains("Effective"))
                                _cpuClocks.Add(s);
                        break;
                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                        Gpus.Add(new GpuState
                        {
                            Name = hw.Name,
                            Hardware = hw,
                            SLoad = Find(hw, SensorType.Load, "GPU Core", "D3D 3D"),
                            STemp = Find(hw, SensorType.Temperature, "GPU Core") ?? First(hw, SensorType.Temperature),
                            SClock = Find(hw, SensorType.Clock, "GPU Core"),
                            SFan = First(hw, SensorType.Fan),
                            SPower = Find(hw, SensorType.Power, "GPU Package", "GPU Power") ?? First(hw, SensorType.Power),
                            SVramUsed = Find(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
                            SVramTotal = Find(hw, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"),
                        });
                        break;
                    case HardwareType.Motherboard:
                        foreach (var sub in hw.SubHardware)
                            foreach (var s in sub.Sensors)
                                if (s.SensorType == SensorType.Fan) { _fans.Add(s); _board = hw; }
                        break;
                }
            }

            // Nvidia cards are read through NVML directly: the generic path costs ~70 ms per update on them.
            try
            {
                if (Nvml.nvmlInit_v2() == 0 && Nvml.nvmlDeviceGetCount_v2(out uint count) == 0)
                {
                    _nvml = true;
                    byte* name = stackalloc byte[96];
                    for (uint i = 0; i < count; i++)
                    {
                        if (Nvml.nvmlDeviceGetHandleByIndex_v2(i, out nint dev) != 0 || Nvml.nvmlDeviceGetName(dev, name, 96) != 0) continue;
                        string text = new((sbyte*)name);
                        foreach (var g in Gpus)
                            if (g.Nvml == 0 && g.Hardware.HardwareType == HardwareType.GpuNvidia && g.Name == text) { g.Nvml = dev; break; }
                    }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            foreach (var g in Gpus) ReadGpu(g, update: false);
            for (int i = 1; i < Gpus.Count; i++)
                if (float.IsNaN(Gpus[GpuIndex].VramTotal) || Gpus[i].VramTotal > Gpus[GpuIndex].VramTotal) GpuIndex = i;
        }
    }

    static void UpdateTree(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    static ISensor? Find(IHardware hw, SensorType type, params ReadOnlySpan<string> names)
    {
        foreach (string name in names)
            foreach (var s in hw.Sensors)
                if (s.SensorType == type && s.Name == name) return s;
        return null;
    }

    static ISensor? First(IHardware hw, SensorType type)
    {
        foreach (var s in hw.Sensors) if (s.SensorType == type) return s;
        return null;
    }

    /// <summary>Sensor value, or NaN when missing. Readings that cannot legitimately be zero pass positiveOnly.</summary>
    static float Val(ISensor? s, bool positiveOnly = false)
        => s?.Value is float v && !float.IsNaN(v) && (positiveOnly ? v > 0 : v >= 0) ? v : float.NaN;

    void ReadGpu(GpuState g, bool update)
    {
        if (g.Nvml != 0)
        {
            nint d = g.Nvml;
            g.Load = Nvml.nvmlDeviceGetUtilizationRates(d, out var util) == 0 ? util.Gpu : float.NaN;
            g.Temp = Nvml.nvmlDeviceGetTemperature(d, 0, out uint temp) == 0 ? temp : float.NaN;
            g.Clock = Nvml.nvmlDeviceGetClockInfo(d, 0, out uint mhz) == 0 && mhz > 0 ? mhz : float.NaN;
            g.Power = Nvml.nvmlDeviceGetPowerUsage(d, out uint mw) == 0 ? mw / 1000f : float.NaN;
            g.FanIsPercent = true;
            g.Fan = Nvml.nvmlDeviceGetFanSpeed(d, out uint pct) == 0 ? pct : float.NaN;
            if (Nvml.nvmlDeviceGetMemoryInfo(d, out var mem) == 0)
            {
                g.VramUsed = mem.Used / 1048576f;
                g.VramTotal = mem.Total / 1048576f;
            }
        }
        else
        {
            if (update) UpdateTree(g.Hardware);
            g.Load = Val(g.SLoad);
            g.Temp = Val(g.STemp, true);
            g.Clock = Val(g.SClock, true);
            g.Fan = Val(g.SFan);
            g.Power = Val(g.SPower);
            g.VramUsed = Val(g.SVramUsed);
            g.VramTotal = Val(g.SVramTotal, true);
        }
        if (g.Clock > g.ClockMax) g.ClockMax = g.Clock;
        if (g.Fan > g.FanMax) g.FanMax = g.Fan;
    }

    void Sample()
    {
        _tick++;
        _pdh!.Collect();

        // CPU load
        Native.GetSystemTimes(out long idle, out long kernel, out long user);
        long total = (kernel - _kernel) + (user - _user);
        if (total > 0) CpuLoad = Math.Clamp(100f * (total - (idle - _idle)) / total, 0, 100);
        _idle = idle; _kernel = kernel; _user = user;
        CpuHistory.Push(CpuLoad);

        // Hardware sensors. Cleared every tick: once the CPU sensors are dropped (no kernel driver), the
        // fallbacks below must still run each time, or the last fallback reading would stick for good.
        CpuTemp = CpuPower = CpuClock = float.NaN;
        string source = "";
        if (_computer != null)
        {
            if (_cpu != null)
            {
                UpdateTree(_cpu);
                foreach (var s in _cpuTemps)
                    if (Val(s, true) is var v && !float.IsNaN(v)) { CpuTemp = v; source = s.Name; break; }
                CpuPower = Val(_sCpuPower, true);
                float sum = 0; int n = 0;
                foreach (var s in _cpuClocks) { float v = Val(s, true); if (!float.IsNaN(v)) { sum += v; n++; } }
                CpuClock = n > 0 ? sum / n : float.NaN;
                // Without the kernel driver these sensors never report; stop paying for the update.
                if (!float.IsNaN(CpuTemp) || !float.IsNaN(CpuPower) || n > 0) _cpuProbes = int.MinValue;
                else if (++_cpuProbes >= 3) _cpu = null;
            }

            CpuFan = float.NaN;
            if (_board != null)
            {
                UpdateTree(_board);
                foreach (var s in _fans)
                {
                    float v = Val(s, true);
                    if (float.IsNaN(v)) continue;
                    if (float.IsNaN(CpuFan) || s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) CpuFan = v;
                }
            }

            // Only the GPU on screen is sampled.
            if (Gpus.Count > 0)
            {
                var g = Gpus[Math.Min(GpuIndex, Gpus.Count - 1)];
                ReadGpu(g, update: true);
                g.History.Push(float.IsNaN(g.Load) ? 0 : g.Load);
            }
        }

        // Fallbacks that work without a kernel driver
        if (float.IsNaN(CpuTemp))
        {
            int count = _pdh.ReadArray(_cZone, out byte* items);
            double kelvin = 0;
            for (int i = 0; i < count; i++)
            {
                byte* it = items + i * PdhQuery.ItemSize;
                if (*(uint*)(it + 8) <= 1) kelvin = Math.Max(kelvin, *(double*)(it + 16));
            }
            if (kelvin > 273) { CpuTemp = (float)(kelvin - 273.15); source = "ACPI thermal zone"; }
        }
        // A reading that never moves is not a reading: catch a board's fixed ACPI number.
        if (source == "ACPI thermal zone")
        {
            if (CpuTemp == _acpiLast) _acpiSame++;
            else { _acpiSame = 0; _acpiLast = CpuTemp; }
        }
        else { _acpiSame = 0; _acpiLast = float.NaN; }
        bool frozen = _acpiSame >= FrozenAfter;
        if (frozen != CpuTempFrozen)
        {
            Log.Write(frozen ? $"cpu temp: the ACPI reading has not changed for {FrozenAfter} s; treating it as stuck" : "cpu temp: the ACPI reading moved again");
            CpuTempFrozen = frozen;
        }
        if (frozen) CpuTemp = float.NaN;
        if (source != CpuTempSource)
        {
            Log.Write($"cpu temp source: {(source == "" ? "none" : source)}{(float.IsNaN(CpuTemp) ? "" : $" ({CpuTemp:0.0} °C)")}");
            CpuTempSource = source;
        }
        if (float.IsNaN(CpuClock) && BaseMhz > 0)
        {
            double perf = _pdh.Value(_cPerf);
            if (perf > 0) CpuClock = (float)(BaseMhz * perf / 100);
        }
        if (CpuClock > CpuClockMax) CpuClockMax = CpuClock;
        if (CpuFan > CpuFanMax) CpuFanMax = CpuFan;

        // Memory
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            MemTotal = mem.ullTotalPhys;
            MemUsed = mem.ullTotalPhys - mem.ullAvailPhys;
            MemLoad = MemTotal > 0 ? 100f * MemUsed / MemTotal : 0;
        }
        MemHistory.Push(MemLoad);

        if (_tick % 30 == 1) FindAdapters();
        SampleNetwork();
        if (_tick % 15 == 1) SampleDrives();

        // Processes
        _gpuByPid.Clear();
        int engines = _pdh.ReadArray(_cGpuEngine, out byte* eng);
        for (int i = 0; i < engines; i++)
        {
            byte* it = eng + i * PdhQuery.ItemSize;
            if (*(uint*)(it + 8) > 1) continue;
            double v = *(double*)(it + 16);
            if (v < 0.05) continue;
            char* name = *(char**)it; // "pid_1234_luid_..."
            if (name == null || name[0] != 'p' || name[3] != '_') continue;
            int pid = 0;
            for (char* c = name + 4; *c >= '0' && *c <= '9'; c++) pid = pid * 10 + (*c - '0');
            _gpuByPid.TryGetValue(pid, out float cur);
            _gpuByPid[pid] = cur + (float)v;
        }
        Processes.Sample(_gpuByPid, _netTrace?.Active == true ? _netTrace.Take() : null);
    }

    /// <summary>
    /// Windows lists every filter driver layered on an adapter as its own interface with the same counters,
    /// and virtual switches re-count traffic that already crossed the physical card.
    /// </summary>
    public static bool IsPhysicalAdapter(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)) return false;
        string d = ni.Description;
        foreach (string token in (ReadOnlySpan<string>)["Filter", "Packet Scheduler", "Miniport", "Kernel Debug", "Virtual", "Bluetooth", "Loopback"])
            if (d.Contains(token, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Enumerating adapters is slow, so it runs rarely; each tick then reads only the adapters found here.</summary>
    void FindAdapters()
    {
        var found = new List<(uint, string, long)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysicalAdapter(ni)) continue;
                var props = ni.GetIPProperties();
                int index;
                try { index = props.GetIPv4Properties().Index; }
                catch (NetworkInformationException) { index = props.GetIPv6Properties().Index; }
                long previous = 0;
                foreach (var old in _nics) if (old.Index == (uint)index) previous = old.Total;
                found.Add(((uint)index, ni.Name, previous));
            }
        }
        catch { return; }

        bool changed = found.Count != _nics.Count;
        for (int i = 0; !changed && i < found.Count; i++) changed = found[i].Item1 != _nics[i].Index;
        _nics.Clear();
        _nics.AddRange(found);
        if (changed) _netTs = 0; // totals are not comparable across a different set of adapters
        if (_nics.Count > 0 && !_nics.Exists(n => n.Name == NetName)) NetName = _nics[0].Name;
    }

    void SampleNetwork()
    {
        const int RowSize = 1352; // MIB_IF_ROW2: +8 InterfaceIndex, +1208 InOctets, +1280 OutOctets
        byte* row = stackalloc byte[RowSize];
        long rx = 0, tx = 0, best = 0;
        string? busiest = null;
        for (int i = 0; i < _nics.Count; i++)
        {
            var nic = _nics[i];
            new Span<byte>(row, RowSize).Clear();
            *(uint*)(row + 8) = nic.Index;
            if (Native.GetIfEntry2(row) != 0) continue;
            long down = *(long*)(row + 1208), up = *(long*)(row + 1280);
            rx += down;
            tx += up;
            if (nic.Total != 0 && down + up - nic.Total > best) { best = down + up - nic.Total; busiest = nic.Name; }
            _nics[i] = (nic.Index, nic.Name, down + up);
        }
        long now = Stopwatch.GetTimestamp();
        if (_netTs != 0)
        {
            double dt = (now - _netTs) / (double)Stopwatch.Frequency;
            NetDown = dt > 0 ? MathF.Max(0, (float)((rx - _rx) / dt)) : 0;
            NetUp = dt > 0 ? MathF.Max(0, (float)((tx - _tx) / dt)) : 0;
        }
        _rx = rx; _tx = tx; _netTs = now;
        if (busiest != null) NetName = busiest;
        DownHistory.Push(NetDown);
        UpHistory.Push(NetUp);
    }

    void SampleDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                Drives.Add(new DriveState { Name = d.Name.TrimEnd('\\'), Total = d.TotalSize, Used = d.TotalSize - d.TotalFreeSpace });
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(1500);
        _netTrace?.Dispose();
        try { _computer?.Close(); } catch { }
        if (_nvml) try { Nvml.nvmlShutdown(); } catch { }
        _pdh?.Dispose();
        Processes.Dispose();
    }
}
