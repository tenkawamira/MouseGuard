using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MouseGuard.Models;
using MouseGuard.Services;

namespace MouseGuard.Native;

internal static class SetupApiInterop
{
    private static readonly Guid MouseClassGuid = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    private static readonly Guid KeyboardClassGuid = new("4d36e96b-e325-11ce-bfc1-08002be10318");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_MFG = 0x0000000B;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    private const uint SPDRP_ENUMERATOR_NAME = 0x00000016;

    private const uint DIF_PROPERTYCHANGE = 0x00000012;
    private const uint DICS_ENABLE = 0x00000001;
    private const uint DICS_DISABLE = 0x00000002;
    private const uint DICS_FLAG_GLOBAL = 0x00000001;

    private const int CR_SUCCESS = 0;
    private const uint DN_STARTED = 0x00000008;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint DN_DISABLEABLE = 0x00002000;
    private const uint CM_PROB_DISABLED = 22;
    private const uint CM_PROB_DEVICE_NOT_THERE = 24;

    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<InputDevice> EnumerateInputDevices()
    {
        var result = new List<InputDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hidNames = HidInterop.GetProductNames();

        EnumerateClass(MouseClassGuid, DeviceKind.Mouse, result, seen, hidNames);
        EnumerateClass(KeyboardClassGuid, DeviceKind.Keyboard, result, seen, hidNames);

        return result
            .OrderBy(d => d.Kind)
            .ThenByDescending(d => d.State == DeviceState.Active)
            .ThenBy(d => d.IsVirtual)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void EnumerateClass(Guid classGuid, DeviceKind kind, List<InputDevice> result, HashSet<string> seen,
        IReadOnlyDictionary<string, string> hidNames)
    {
        var set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var instanceId = GetInstanceId(set, ref data);
                if (string.IsNullOrWhiteSpace(instanceId) || !seen.Add(instanceId))
                    continue;

                var friendly = GetProperty(set, ref data, SPDRP_FRIENDLYNAME);
                var description = GetProperty(set, ref data, SPDRP_DEVICEDESC);
                var manufacturer = GetProperty(set, ref data, SPDRP_MFG) ?? string.Empty;
                var enumerator = GetProperty(set, ref data, SPDRP_ENUMERATOR_NAME) ?? string.Empty;

                var rawName = !string.IsNullOrWhiteSpace(friendly) ? friendly : description;
                if (string.IsNullOrWhiteSpace(rawName))
                    rawName = kind == DeviceKind.Mouse ? "HID 鼠标" : "HID 键盘";

                var (status, problem) = GetStatus(data.DevInst);
                var state = status switch
                {
                    _ when status < 0 => DeviceState.Unavailable,
                    _ when (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED => DeviceState.Disabled,
                    _ when (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DEVICE_NOT_THERE => DeviceState.Unavailable,
                    _ when (status & DN_STARTED) == 0 => DeviceState.Unavailable,
                    _ => DeviceState.Active
                };

                var isVirtual = instanceId.Contains("VHF", StringComparison.OrdinalIgnoreCase)
                                || instanceId.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase)
                                || instanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
                                || rawName.Contains("虚拟", StringComparison.OrdinalIgnoreCase)
                                || manufacturer.Contains("Netease", StringComparison.OrdinalIgnoreCase);

                var chain = GetDeviceChain(data.DevInst, instanceId);
                var (vid, pid) = ParseVidPid(instanceId);
                hidNames.TryGetValue(instanceId, out var hidProduct);
                var displayName = PickDisplayName(rawName, chain, kind, instanceId, manufacturer, vid, pid, hidProduct);
                var connection = DetectConnection(instanceId, chain, enumerator, isVirtual);

                result.Add(new InputDevice
                {
                    Id = instanceId,
                    InstanceId = instanceId,
                    Name = displayName,
                    Manufacturer = manufacturer,
                    Kind = kind,
                    State = state,
                    IsVirtual = isVirtual,
                    Connection = connection,
                    VendorId = vid,
                    ProductId = pid,
                    Disableable = (status & DN_DISABLEABLE) != 0
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public static bool SetDeviceEnabled(string instanceId, DeviceKind kind, bool enabled, out string error)
    {
        error = string.Empty;
        if (!IsAdministrator)
        {
            error = "需要管理员权限才能修改设备状态。";
            return false;
        }

        var classGuid = kind == DeviceKind.Mouse ? MouseClassGuid : KeyboardClassGuid;
        var set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            error = "无法打开设备信息集。";
            return false;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!string.Equals(GetInstanceId(set, ref data), instanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var header = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = DIF_PROPERTYCHANGE
                };

                var change = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = header,
                    StateChange = enabled ? DICS_ENABLE : DICS_DISABLE,
                    Scope = DICS_FLAG_GLOBAL,
                    HwProfile = 0
                };

                var size = Marshal.SizeOf<SP_PROPCHANGE_PARAMS>();
                if (!SetupDiSetClassInstallParams(set, ref data, ref change, size) ||
                    !SetupDiChangeState(set, ref data))
                {
                    var apiError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    if (TryPnPUtil(instanceId, enabled))
                        return true;

                    error = apiError;
                    return false;
                }

                return true;
            }

            error = "系统中已找不到该设备。";
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool TryPnPUtil(string instanceId, bool enabled)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(enabled ? "/enable-device" : "/disable-device");
            psi.ArgumentList.Add(instanceId);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogService.Warn("pnputil 回退失败: " + ex.Message);
            return false;
        }
    }

    private static (int Status, uint Problem) GetStatus(uint devInst)
    {
        var result = CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        return result == CR_SUCCESS ? ((int)status, problem) : (-1, 0);
    }

    private static string PickDisplayName(
        string rawName,
        IReadOnlyList<string> chain,
        DeviceKind kind,
        string instanceId,
        string manufacturer,
        string vid,
        string pid,
        string? hidProduct)
    {
        if (!IsGenericName(hidProduct))
            return hidProduct!.Trim();

        foreach (var name in chain)
        {
            if (!IsGenericName(name) && !IsBusName(name))
                return name.Trim();
        }

        if (!IsGenericName(rawName) && !IsBusName(rawName))
            return rawName.Trim();

        if (!string.IsNullOrWhiteSpace(manufacturer) && !IsGenericName(manufacturer))
            return $"{manufacturer.Trim()} {KindName(kind)}";

        if (!string.IsNullOrWhiteSpace(vid) && !string.IsNullOrWhiteSpace(pid))
            return $"{KindName(kind)} ({vid}:{pid})";

        return rawName;
    }

    private static string KindName(DeviceKind kind) => kind == DeviceKind.Mouse ? "鼠标" : "键盘";

    private static bool IsBusName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var n = name.Trim();
        return n.Contains("Root Hub", StringComparison.OrdinalIgnoreCase)
               || n.Contains("根集线器", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Generic USB Hub", StringComparison.OrdinalIgnoreCase)
               || n.Contains("USB Hub", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Composite Device", StringComparison.OrdinalIgnoreCase)
               || n.Contains("复合设备", StringComparison.OrdinalIgnoreCase)
               || n.Contains("USB 集线器", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var n = name.Trim();
        return n.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase)
               || n.Contains("符合 HID", StringComparison.OrdinalIgnoreCase)
               || n.Contains("USB Input Device", StringComparison.OrdinalIgnoreCase)
               || n.Contains("USB 输入设备", StringComparison.OrdinalIgnoreCase)
               || n.Contains("HID Keyboard Device", StringComparison.OrdinalIgnoreCase)
               || n.Contains("HID 键盘设备", StringComparison.OrdinalIgnoreCase)
               || n.Contains("HID-compliant mouse", StringComparison.OrdinalIgnoreCase)
               || n.Contains("HID-compliant keyboard", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Virtual HID Framework", StringComparison.OrdinalIgnoreCase)
               || n.Contains("虚拟 HID 框架", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
               || n.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
               || n.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)
               || n.Equals("鼠标", StringComparison.OrdinalIgnoreCase)
               || n.Equals("键盘", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetDeviceChain(uint devInst, string startInstance)
    {
        var names = new List<string>();
        var current = devInst;
        var currentId = startInstance;

        for (var depth = 0; depth < 4; depth++)
        {
            var name = ReadFriendlyNameFromRegistry(currentId);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);

            if (CM_Get_Parent(out var parent, current, 0) != CR_SUCCESS)
                break;

            var sb = new StringBuilder(512);
            if (CM_Get_Device_ID(parent, sb, sb.Capacity, 0) != CR_SUCCESS)
                break;

            current = parent;
            currentId = sb.ToString();
        }

        return names;
    }

    private static string? ReadFriendlyNameFromRegistry(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            if (key is null)
                return null;

            var friendly = key.GetValue("FriendlyName") as string;
            if (!string.IsNullOrWhiteSpace(friendly))
                return friendly;

            var desc = key.GetValue("DeviceDesc") as string;
            if (string.IsNullOrWhiteSpace(desc))
                return null;

            var index = desc.LastIndexOf(';');
            return index >= 0 ? desc[(index + 1)..] : desc;
        }
        catch
        {
            return null;
        }
    }

    private static string DetectConnection(string instanceId, IReadOnlyList<string> chain, string enumerator, bool isVirtual)
    {
        if (isVirtual)
            return "虚拟设备";

        var haystack = instanceId + " " + string.Join(" ", chain);
        if (haystack.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("蓝牙", StringComparison.OrdinalIgnoreCase))
            return "蓝牙";

        if (instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return "USB";

        if (instanceId.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase))
            return "内置";

        if (!string.IsNullOrWhiteSpace(enumerator))
            return enumerator;

        return "HID";
    }

    private static (string Vid, string Pid) ParseVidPid(string instanceId)
    {
        var vid = Regex.Match(instanceId, @"VID_([0-9A-Fa-f]{4})");
        var pid = Regex.Match(instanceId, @"PID_([0-9A-Fa-f]{4})");
        return (vid.Success ? vid.Groups[1].Value.ToUpperInvariant() : string.Empty,
                pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : string.Empty);
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : string.Empty;
    }

    private static string? GetProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size))
            return null;

        if (size == 0)
            return null;

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiChangeState(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, int bufferLen, uint flags);
}
