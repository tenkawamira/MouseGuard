namespace MouseGuard.Models;

public sealed class InputDevice
{
    public string Id { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public DeviceKind Kind { get; init; }
    public DeviceState State { get; init; }
    public bool IsVirtual { get; init; }
    public string Connection { get; init; } = "HID";
    public string VendorId { get; init; } = string.Empty;
    public string ProductId { get; init; } = string.Empty;
    public bool Disableable { get; init; }

    public string KindLabel => Kind == DeviceKind.Mouse ? "鼠标" : "键盘";

    public string ConnectionLabel => IsVirtual ? "虚拟设备" : Connection;
}
