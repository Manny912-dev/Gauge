using System.Runtime.InteropServices;

namespace Gauge;

/// <summary>Thin wrapper over a PDH query. Avoids System.Diagnostics.PerformanceCounter, which allocates heavily.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    public const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name ptr, status, value

    nint _query;
    byte* _buf;
    uint _bufLen;

    public PdhQuery()
    {
        if (Native.PdhOpenQueryW(null, 0, out _query) != 0) _query = 0;
    }

    public nint Add(string path)
        => _query != 0 && Native.PdhAddEnglishCounterW(_query, path, 0, out nint c) == 0 ? c : 0;

    public bool Collect() => _query != 0 && Native.PdhCollectQueryData(_query) == 0;

    public double Value(nint counter)
    {
        if (counter == 0) return double.NaN;
        byte* v = stackalloc byte[16];
        if (Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, null, v) != 0) return double.NaN;
        return *(uint*)v > 1 ? double.NaN : *(double*)(v + 8);
    }

    /// <summary>Reads every instance of a wildcard counter. Returned items are valid until the next call.</summary>
    public int ReadArray(nint counter, out byte* items)
    {
        items = null;
        if (counter == 0) return 0;
        const uint fmt = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
        uint size = _bufLen;
        uint rc = Native.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out uint count, _buf);
        if (rc == Native.PDH_MORE_DATA)
        {
            if (_buf != null) NativeMemory.Free(_buf);
            _bufLen = size + 16384;
            _buf = (byte*)NativeMemory.Alloc(_bufLen);
            size = _bufLen;
            rc = Native.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out count, _buf);
        }
        if (rc != 0) return 0;
        items = _buf;
        return (int)count;
    }

    public void Dispose()
    {
        if (_query != 0) { Native.PdhCloseQuery(_query); _query = 0; }
        if (_buf != null) { NativeMemory.Free(_buf); _buf = null; }
    }
}
