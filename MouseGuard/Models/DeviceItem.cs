using MouseGuard.Helpers;

namespace MouseGuard.Models;

public sealed class DeviceItem : ObservableObject
{
    private InputDevice _device;
    private bool _isTakenOver;
    private bool _isBusy;
    private string _alias = string.Empty;

    public DeviceItem(InputDevice device, string alias = "")
    {
        _device = device;
        _alias = alias;
    }

    public InputDevice Device => _device;

    public string Id => _device.Id;
    public string InstanceId => _device.InstanceId;
    public DeviceKind Kind => _device.Kind;
    public string KindLabel => _device.KindLabel;
    public bool IsVirtual => _device.IsVirtual;
    public bool Disableable => _device.Disableable;
    public DeviceState State => _device.State;
    public bool IsActive => _device.State == DeviceState.Active;

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? _device.Name : Alias;

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { _device.KindLabel, _device.ConnectionLabel };
            if (!string.IsNullOrWhiteSpace(_device.Manufacturer) &&
                !_device.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                parts.Add(_device.Manufacturer);
            return string.Join(" · ", parts);
        }
    }

    public string DevicePath => _device.InstanceId;

    public string VendorLabel
    {
        get
        {
            if (string.IsNullOrEmpty(_device.VendorId))
                return "HID";
            return $"{_device.VendorId}:{_device.ProductId}";
        }
    }

    public bool IsTakenOver
    {
        // The managed-device list is the source of truth. A device can be
        // reported as Disabled for a short moment after it was released, and
        // OR-ing that state into this property made the switch jump back on.
        get => _isTakenOver;
        set
        {
            if (SetProperty(ref _isTakenOver, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusTone));
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(IsAvailable));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsAvailable => IsActive && !IsTakenOver;

    public string StatusText => IsTakenOver
        ? "已接管"
        : _device.State switch
        {
            DeviceState.Disabled => "已禁用",
            DeviceState.Unavailable => "未连接",
            _ => "可用"
        };

    public StatusTone StatusTone => IsTakenOver
        ? StatusTone.Warning
        : _device.State switch
        {
            DeviceState.Disabled => StatusTone.Muted,
            DeviceState.Unavailable => StatusTone.Muted,
            _ => StatusTone.Success
        };

    public string ToggleLabel => IsTakenOver ? "释放" : "接管";

    public string KindGlyph => Kind == DeviceKind.Mouse ? "\uE962" : "\uE765";

    public void Update(InputDevice device)
    {
        _device = device;
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(IsTakenOver));
        OnPropertyChanged(nameof(Disableable));
        OnPropertyChanged(nameof(DevicePath));
        OnPropertyChanged(nameof(VendorLabel));
    }

    public string AccessibleName => $"{DisplayName}，{StatusText}";
}
