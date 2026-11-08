using System.Text.RegularExpressions;
using MouseGuard.Models;
using MouseGuard.Native;

namespace MouseGuard.Services;

public sealed class DeviceService
{
    private Dictionary<string, InputDevice> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<InputDevice> _devices = new();

    public string? ResolveRawPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        var normalized = NormalizeRawPath(rawPath);
        if (_byId.TryGetValue(normalized, out var exact))
            return exact.Id;

        foreach (var device in _devices)
        {
            if (normalized.StartsWith(device.Id, StringComparison.OrdinalIgnoreCase) ||
                device.Id.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                return device.Id;
        }

        var (vid, pid) = ParseVidPid(normalized);
        if (vid.Length > 0 && pid.Length > 0)
        {
            var match = _devices.FirstOrDefault(d =>
                string.Equals(d.VendorId, vid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.ProductId, pid, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match.Id;
        }

        return null;
    }

    public IReadOnlyList<InputDevice> Refresh()
    {
        var devices = SetupApiInterop.EnumerateInputDevices();
        _devices = devices.ToList();
        _byId = devices
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return devices;
    }

    public (bool Success, string Error) SetEnabled(string instanceId, DeviceKind kind, bool enabled)
    {
        var result = SetupApiInterop.SetDeviceEnabled(instanceId, kind, enabled, out var error);
        if (!result)
            LogService.Warn($"修改设备状态失败 ({instanceId}): {error}");
        return (result, error);
    }

    public static string NormalizeRawPath(string rawPath)
    {
        var value = rawPath.Trim();
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            value = value[4..];

        var interfaceIndex = value.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceIndex >= 0)
            value = value[..interfaceIndex];

        return value.Replace('#', '\\');
    }

    private static (string Vid, string Pid) ParseVidPid(string text)
    {
        var vid = Regex.Match(text, @"VID_([0-9A-Fa-f]{4})");
        var pid = Regex.Match(text, @"PID_([0-9A-Fa-f]{4})");
        return (vid.Success ? vid.Groups[1].Value.ToUpperInvariant() : string.Empty,
                pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : string.Empty);
    }
}
