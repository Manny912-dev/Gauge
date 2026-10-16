using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gauge;

internal sealed class ProcRow
{
    public string Name = "";
    public float Cpu, Gpu, Down, Up;
    public long Mem;
    public int Count;
}

/// <summary>Process list grouped by image name, read in one NtQuerySystemInformation call per tick.</summary>
internal sealed unsafe class ProcessSampler : IDisposable
{
    struct PidState { public long Create, Cpu; public string Name; public int Tick; }

    static readonly Comparison<ProcRow>[] s_sorts =
    [
        static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        static (a, b) => Desc(a.Cpu, b.Cpu, a, b),
        static (a, b) => Desc(a.Gpu, b.Gpu, a, b),
        static (a, b) => Desc(a.Mem, b.Mem, a, b),
        static (a, b) => Desc(a.Down, b.Down, a, b),
        static (a, b) => Desc(a.Up, b.Up, a, b),
    ];

    readonly Dictionary<int, PidState> _pids = new();
    readonly Dictionary<string, ProcRow> _groups = new();
    readonly List<int> _deadPids = new();
    readonly List<string> _deadGroups = new();
    readonly int _ncpu = Environment.ProcessorCount;
    byte* _buf;
    uint _len = 1 << 19;
    long _lastTs;
    int _tick;

    public readonly List<ProcRow> Rows = new();
    public int SortKey = 1;
    public int ProcessCount, ThreadCount;

    static int Desc(double x, double y, ProcRow a, ProcRow b)
    {
        int c = y.CompareTo(x);
        if (c == 0) c = b.Mem.CompareTo(a.Mem);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    public void Sort() => Rows.Sort(s_sorts[SortKey]);

    public void Sample(Dictionary<int, float>? gpu, Dictionary<int, (long Down, long Up)>? net)
    {
        if (_buf == null) _buf = (byte*)NativeMemory.Alloc(_len);
        int status;
        while ((status = Native.NtQuerySystemInformation(5, _buf, _len, out uint needed)) == unchecked((int)0xC0000004))
        {
            NativeMemory.Free(_buf);
            _len = needed + 65536;
            _buf = (byte*)NativeMemory.Alloc(_len);
        }
        if (status != 0) return;

        long now = Stopwatch.GetTimestamp();
        double dt = _lastTs == 0 ? 0 : (now - _lastTs) / (double)Stopwatch.Frequency;
        _lastTs = now;
        _tick++;

        foreach (var g in _groups.Values) { g.Cpu = g.Gpu = g.Down = g.Up = 0; g.Mem = 0; g.Count = 0; }

        // SYSTEM_PROCESS_INFORMATION (x64): +8 private working set, +32 create time, +40 user time,
        // +48 kernel time, +56 image name (UNICODE_STRING), +80 pid.
        int count = 0, threads = 0;
        for (byte* p = _buf; ; p += *(uint*)p)
        {
            int pid = (int)*(long*)(p + 80);
            if (pid != 0)
            {
                count++;
                threads += (int)*(uint*)(p + 4);
                long create = *(long*)(p + 32);
                long cpu = *(long*)(p + 40) + *(long*)(p + 48);
                ref PidState s = ref CollectionsMarshal.GetValueRefOrAddDefault(_pids, pid, out bool existed);
                float pct = 0;
                if (!existed || s.Create != create)
                {
                    s.Name = MakeName(*(char**)(p + 64), *(ushort*)(p + 56) / 2, pid);
                    s.Create = create;
                }
                else if (dt > 0)
                {
                    pct = (float)((cpu - s.Cpu) / (dt * 1e7 * _ncpu) * 100);
                }
                s.Cpu = cpu;
                s.Tick = _tick;

                if (!_groups.TryGetValue(s.Name, out var row))
                    _groups.Add(s.Name, row = new ProcRow { Name = s.Name });
                row.Count++;
                row.Cpu += MathF.Max(pct, 0);
                row.Mem += *(long*)(p + 8);
                if (gpu != null && gpu.TryGetValue(pid, out float gv)) row.Gpu = MathF.Min(100, row.Gpu + gv);
                if (net != null && dt > 0 && net.TryGetValue(pid, out var nv))
                {
                    row.Down += (float)(nv.Down / dt);
                    row.Up += (float)(nv.Up / dt);
                }
            }
            if (*(uint*)p == 0) break;
        }
        ProcessCount = count;
        ThreadCount = threads;

        _deadPids.Clear();
        foreach (var kv in _pids) if (kv.Value.Tick != _tick) _deadPids.Add(kv.Key);
        foreach (int pid in _deadPids) _pids.Remove(pid);
        _deadGroups.Clear();
        foreach (var kv in _groups) if (kv.Value.Count == 0) _deadGroups.Add(kv.Key);
        foreach (string name in _deadGroups) _groups.Remove(name);

        Rows.Clear();
        Rows.AddRange(_groups.Values);
        Sort();
    }

    static string MakeName(char* chars, int length, int pid)
    {
        if (chars == null || length <= 0) return pid == 4 ? "System" : "Process " + pid;
        var span = new ReadOnlySpan<char>(chars, length);
        if (span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) span = span[..^4];
        return span.ToString();
    }

    public void Dispose()
    {
        if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
    }
}
