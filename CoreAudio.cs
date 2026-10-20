using System.Runtime.InteropServices;

namespace AirPodsHandoff;

// Minimal Core Audio / KS COM interop. Method order in each interface must match the vtable,
// so unused methods are declared too.

enum EDataFlow { Render = 0, Capture = 1, All = 2 }
enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

static class DeviceState
{
    public const uint Active = 0x1;
    public const uint Unplugged = 0x8;
}

static class ClsCtx
{
    public const uint All = 0x17;
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public static readonly PropertyKey DeviceFriendlyName =
        new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort vt;
    public ushort reserved1, reserved2, reserved3;
    public IntPtr pointer;
    public IntPtr pointer2;

    public const ushort VT_LPWSTR = 31;
}

[StructLayout(LayoutKind.Sequential)]
struct KsPropertyHeader
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(uint access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    void GetConnectorType(out int type);
    void GetDataFlow(out int flow);
    void ConnectTo(IConnector other);
    void Disconnect();
    void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    void GetConnectedTo(out IConnector other);
    void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig]
    int KsProperty(ref KsPropertyHeader property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
}

static class CoreAudio
{
    public static IMMDeviceEnumerator NewEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static T Activate<T>(IMMDevice device)
    {
        var iid = typeof(T).GUID;
        device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var iface);
        return (T)iface;
    }

    public static string FriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(0 /* STGM_READ */, out var store);
        var key = PropertyKey.DeviceFriendlyName;
        store.GetValue(ref key, out var value);
        try
        {
            return value.vt == PropVariant.VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) ?? "" : "";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);
}
