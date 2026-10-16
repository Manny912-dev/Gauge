using System.Runtime.InteropServices;

namespace Gauge;

/// <summary>Nvidia Management Library, installed with the Nvidia driver. All calls return 0 on success.</summary>
internal static unsafe partial class Nvml
{
    internal struct Utilization { public uint Gpu, Memory; }
    internal struct Memory { public ulong Total, Free, Used; }

    [LibraryImport("nvml.dll")] internal static partial int nvmlInit_v2();
    [LibraryImport("nvml.dll")] internal static partial int nvmlShutdown();
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetName(nint device, byte* name, uint length);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetUtilizationRates(nint device, out Utilization utilization);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetTemperature(nint device, int sensor, out uint celsius);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetClockInfo(nint device, int clockType, out uint mhz);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetFanSpeed(nint device, out uint percent);
    [LibraryImport("nvml.dll")] internal static partial int nvmlDeviceGetMemoryInfo(nint device, out Memory memory);
}
