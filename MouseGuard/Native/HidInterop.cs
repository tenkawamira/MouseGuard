using System.Runtime.InteropServices;
using System.Text;

namespace MouseGuard.Native;

/// <summary>
/// Reads the product string of HID collections. Windows often exposes generic
/// names such as "HID-compliant mouse" for the child device; the HID product
/// string gives the actual model name (e.g. "MX Master 3S").
/// </summary>
internal static class HidInterop
{
    private static readonly Guid HidInterfaceGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const int ERROR_NO_MORE_ITEMS = 259;

    public static Dictionary<string, string> GetProductNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var interfaceGuid = HidInterfaceGuid;
        var set = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return result;

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData); index++)
            {
                var detail = GetInterfaceDetail(set, ref interfaceData);
                if (detail is null)
                    continue;

                var (path, deviceInfo) = detail.Value;
                var instanceId = GetInstanceId(set, ref deviceInfo);
                if (string.IsNullOrWhiteSpace(instanceId) || result.ContainsKey(instanceId))
                    continue;

                var product = ReadString(path, isProduct: true);
                var manufacturer = ReadString(path, isProduct: false);

                var name = !string.IsNullOrWhiteSpace(product) ? product.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(manufacturer))
                    name = manufacturer.Trim() + " HID";

                if (!string.IsNullOrWhiteSpace(name))
                    result[instanceId] = name;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static (string Path, SP_DEVINFO_DATA DeviceInfo)? GetInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        var required = 0u;
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required == 0 || required > 8192)
            return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize is 4 bytes on x86; on x64 the structure is 8-byte aligned.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
            var deviceInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, ref required, ref deviceInfo))
                return null;

            var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return (path, deviceInfo);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadString(string path, bool isProduct)
    {
        var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return null;

        try
        {
            var buffer = new StringBuilder(256);
            var ok = isProduct
                ? HidD_GetProductString(handle, buffer, buffer.Capacity)
                : HidD_GetManufacturerString(handle, buffer, buffer.Capacity);
            return ok ? buffer.ToString().TrimEnd('\0') : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        ref uint requiredSize,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        ref uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);
}
