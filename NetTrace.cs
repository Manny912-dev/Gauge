using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gauge;

/// <summary>
/// Per-process network byte counts from the kernel TCP/IP ETW provider.
/// Needs administrator rights; when unavailable <see cref="Active"/> stays false.
/// </summary>
internal sealed unsafe class NetTrace : IDisposable
{
    const string SessionName = "Gauge-NetTrace";
    const int PropsSize = 120;                 // sizeof(EVENT_TRACE_PROPERTIES)
    const int LogfileSize = 448;               // sizeof(EVENT_TRACE_LOGFILEW) on x64
    static readonly Guid SessionGuid = new("6f0f1a3e-58a4-4c6e-9d1b-2f6c0e3a7b51");
    static readonly Guid TcpIp = new("9A280AC0-C8E0-11D1-84E2-00C04FB998A2");
    static readonly Guid UdpIp = new("BF3A50C5-A9C9-4988-A005-2DF0B7C80F80");

    static NetTrace? s_current;

    readonly object _lock = new();
    Dictionary<int, (long Down, long Up)> _acc = new(), _spare = new();
    ulong _session, _trace = ulong.MaxValue;
    Thread? _thread;

    public bool Active { get; private set; }

    public bool Start()
    {
        int size = PropsSize + (SessionName.Length + 1) * 2;
        byte* props = (byte*)NativeMemory.Alloc((nuint)size);
        byte* logfile = (byte*)NativeMemory.AllocZeroed(LogfileSize);
        try
        {
            FillProps(props, size);
            uint rc = Native.StartTraceW(out _session, SessionName, props);
            if (rc == 183) // left over from a previous run that did not exit cleanly
            {
                FillProps(props, size);
                Native.ControlTraceW(0, SessionName, props, 1);
                FillProps(props, size);
                rc = Native.StartTraceW(out _session, SessionName, props);
            }
            if (rc != 0) { _session = 0; return false; }

            fixed (char* name = SessionName)
            {
                *(char**)(logfile + 8) = name;                              // LoggerName
                *(uint*)(logfile + 28) = 0x00000100 | 0x10000000;           // real time | event record
                *(nint*)(logfile + 424) = (nint)(delegate* unmanaged[Stdcall]<byte*, void>)&OnEvent;
                s_current = this;
                _trace = Native.OpenTraceW(logfile);
            }
            if (_trace == ulong.MaxValue) { StopSession(); return false; }

            _thread = new Thread(Pump) { IsBackground = true, Name = "NetTrace" };
            _thread.Start();
            Active = true;
            return true;
        }
        finally
        {
            NativeMemory.Free(props);
            NativeMemory.Free(logfile);
        }
    }

    static void FillProps(byte* p, int size)
    {
        new Span<byte>(p, size).Clear();
        *(uint*)p = (uint)size;                 // Wnode.BufferSize
        *(Guid*)(p + 24) = SessionGuid;         // Wnode.Guid
        *(uint*)(p + 40) = 1;                   // Wnode.ClientContext: QPC
        *(uint*)(p + 44) = 0x00020000;          // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 48) = 64;                  // BufferSize (KB)
        *(uint*)(p + 64) = 0x00000100 | 0x02000000; // real time | system logger mode
        *(uint*)(p + 68) = 1;                   // FlushTimer (s)
        *(uint*)(p + 72) = 0x00010000;          // EnableFlags: EVENT_TRACE_FLAG_NETWORK_TCPIP
        *(uint*)(p + 116) = PropsSize;          // LoggerNameOffset
    }

    void Pump()
    {
        ulong h = _trace;
        Native.ProcessTrace(&h, 1, null, null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void OnEvent(byte* record)
    {
        try
        {
            byte opcode = record[45];
            bool send = opcode is 10 or 26, recv = opcode is 11 or 27;
            if (!send && !recv) return;
            if (*(ushort*)(record + 86) < 8) return;
            Guid* provider = (Guid*)(record + 24);
            if (*provider != TcpIp && *provider != UdpIp) return;
            byte* data = *(byte**)(record + 96);
            int pid = (int)*(uint*)data;
            uint bytes = *(uint*)(data + 4);
            var self = s_current;
            if (self == null) return;
            lock (self._lock)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(self._acc, pid, out _);
                if (send) slot.Up += bytes; else slot.Down += bytes;
            }
        }
        catch { }
    }

    /// <summary>Returns bytes seen per pid since the previous call. The dictionary is reused on the call after next.</summary>
    public Dictionary<int, (long Down, long Up)> Take()
    {
        lock (_lock)
        {
            var result = _acc;
            _spare.Clear();
            _acc = _spare;
            _spare = result;
            return result;
        }
    }

    void StopSession()
    {
        int size = PropsSize + (SessionName.Length + 1) * 2;
        byte* props = (byte*)NativeMemory.Alloc((nuint)size);
        FillProps(props, size);
        Native.ControlTraceW(_session, null, props, 1);
        NativeMemory.Free(props);
        _session = 0;
    }

    public void Dispose()
    {
        Active = false;
        if (_session != 0) StopSession();
        if (_trace != ulong.MaxValue) { Native.CloseTrace(_trace); _trace = ulong.MaxValue; }
        _thread?.Join(500);
        if (s_current == this) s_current = null;
    }
}
