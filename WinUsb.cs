using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gauge;

/// <summary>
/// A bulk OUT pipe on a WinUSB interface, opened by its device-interface GUID. Used for the
/// NZXT Kraken's LCD image data; its small commands go over HID instead.
/// </summary>
internal sealed unsafe partial class WinUsbPipe : IDisposable
{
    readonly SafeFileHandle _file;
    nint _usb;
    readonly byte _pipe;

    WinUsbPipe(SafeFileHandle file, nint usb, byte pipe) { _file = file; _usb = usb; _pipe = pipe; }

    /// <summary>Opens the first present device with this interface GUID and finds its bulk OUT pipe. Null if none.</summary>
    /// <exception cref="IOException">The device exists but could not be opened (another program holds it).</exception>
    public static WinUsbPipe? Open(Guid interfaceGuid, int timeoutMs)
    {
        string? path = FindPath(interfaceGuid);
        if (path == null) return null;
        var file = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, FileAttributeNormal | FileFlagOverlapped, 0);
        if (file.IsInvalid) throw new IOException($"the cooler's USB interface is in use (error {Marshal.GetLastPInvokeError()})");
        if (!WinUsb_Initialize(file, out nint usb))
        {
            int error = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new IOException($"could not start WinUSB on the cooler (error {error})");
        }
        try
        {
            if (!WinUsb_QueryInterfaceSettings(usb, 0, out var iface)) throw new IOException("could not read the cooler's USB interface");
            for (byte i = 0; i < iface.NumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(usb, 0, i, out var pipe)) continue;
                if (pipe.PipeType == PipeTypeBulk && (pipe.PipeId & 0x80) == 0)
                {
                    uint ms = (uint)timeoutMs;
                    WinUsb_SetPipePolicy(usb, pipe.PipeId, PipeTransferTimeout, sizeof(uint), &ms);
                    return new WinUsbPipe(file, usb, pipe.PipeId);
                }
            }
            throw new IOException("the cooler has no bulk output pipe");
        }
        catch
        {
            WinUsb_Free(usb);
            file.Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            if (!WinUsb_WritePipe(_usb, _pipe, p, (uint)data.Length, out uint written, 0))
                throw new IOException($"USB transfer to the cooler failed (error {Marshal.GetLastPInvokeError()})");
            if (written != data.Length) throw new IOException($"USB transfer to the cooler stopped at {written} of {data.Length} bytes");
        }
    }

    public void Dispose()
    {
        if (_usb != 0) { WinUsb_Free(_usb); _usb = 0; }
        _file.Dispose();
    }

    static string? FindPath(Guid guid)
    {
        nint set = SetupDiGetClassDevsW(ref guid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1) return null;
        try
        {
            var data = new SpDeviceInterfaceData { Size = sizeof(SpDeviceInterfaceData) };
            if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetailW(set, ref data, null, 0, out uint needed, 0);
            if (needed == 0) return null;
            var buffer = new byte[needed];
            fixed (byte* b = buffer)
            {
                *(int*)b = sizeof(nint) == 8 ? 8 : 6; // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, b, needed, out _, 0)) return null;
                return new string((char*)(b + 4));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    const uint FileAttributeNormal = 0x80, FileFlagOverlapped = 0x40000000;
    const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10, PipeTransferTimeout = 0x03;
    const int PipeTypeBulk = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public nint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct UsbInterfaceDescriptor
    {
        public byte Length, DescriptorType, InterfaceNumber, AlternateSetting, NumEndpoints, InterfaceClass, InterfaceSubClass, InterfaceProtocol, Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PipeInformation { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiGetClassDevsW(ref Guid classGuid, nint enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData data, byte* detail, uint size, out uint required, nint info);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Initialize(SafeFileHandle file, out nint usb);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Free(nint usb);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryInterfaceSettings(nint usb, byte alternate, out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_QueryPipe(nint usb, byte alternate, byte index, out PipeInformation pipe);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_SetPipePolicy(nint usb, byte pipe, uint policy, uint length, void* value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_WritePipe(nint usb, byte pipe, byte* buffer, uint length, out uint written, nint overlapped);
}
