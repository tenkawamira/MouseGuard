using System.Text.Json.Serialization;
using MouseGuard.Helpers;

namespace MouseGuard.Models;

public sealed class PopupSettings : ObservableObject
{
    private string _title = "鼠标已被接管";
    private string _message = "此设备的鼠标控制权已被接管";
    private int _durationSeconds = 6;
    private PopupPosition _position = PopupPosition.Center;
    private bool _showDeviceName = true;
    private bool _showFooterBranding;
    private string _footerText = "鼠标卫士";
    private string _accent = "Blue";

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public int DurationSeconds
    {
        get => _durationSeconds;
        set => SetProperty(ref _durationSeconds, Math.Clamp(value, 2, 30));
    }

    public PopupPosition Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public bool ShowDeviceName
    {
        get => _showDeviceName;
        set => SetProperty(ref _showDeviceName, value);
    }

    /// <summary>
    /// Optional small signature in the popup footer. Off by default so the
    /// notification stays neutral and does not advertise the app during class.
    /// </summary>
    public bool ShowFooterBranding
    {
        get => _showFooterBranding;
        set => SetProperty(ref _showFooterBranding, value);
    }

    public string FooterText
    {
        get => _footerText;
        set => SetProperty(ref _footerText, value);
    }

    public string Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, value);
    }

    public PopupSettings Clone() => new()
    {
        Title = Title,
        Message = Message,
        DurationSeconds = DurationSeconds,
        Position = Position,
        ShowDeviceName = ShowDeviceName,
        ShowFooterBranding = ShowFooterBranding,
        FooterText = FooterText,
        Accent = Accent
    };
}

public sealed class AppSettings : ObservableObject
{
    private bool _launchAtStartup = true;
    private bool _startMinimized;
    private bool _minimizeToTray = true;
    private bool _showTakeoverPopup = true;
    private bool _useSystemNotifications;
    private bool _autoReleaseOnReconnect = true;
    private AppTheme _theme = AppTheme.Dark;
    private PopupSettings _popup = new();
    private List<HotkeyBinding> _hotkeys = new();
    private Dictionary<string, string> _deviceAliases = new();
    private List<string> _managedDeviceIds = new();
    private bool _hasCompletedFirstRun;

    public int Version { get; set; } = 2;

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool ShowTakeoverPopup
    {
        get => _showTakeoverPopup;
        set => SetProperty(ref _showTakeoverPopup, value);
    }

    /// <summary>
    /// Send a native Windows toast instead of the in-app takeover card.
    /// </summary>
    public bool UseSystemNotifications
    {
        get => _useSystemNotifications;
        set => SetProperty(ref _useSystemNotifications, value);
    }

    /// <summary>
    /// When a receiver disappears and comes back, release the device that was
    /// under control instead of leaving it disabled or locked.
    /// </summary>
    public bool AutoReleaseOnReconnect
    {
        get => _autoReleaseOnReconnect;
        set => SetProperty(ref _autoReleaseOnReconnect, value);
    }

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public PopupSettings Popup
    {
        get => _popup;
        set => SetProperty(ref _popup, value);
    }

    public List<HotkeyBinding> Hotkeys
    {
        get => _hotkeys;
        set => SetProperty(ref _hotkeys, value);
    }

    public Dictionary<string, string> DeviceAliases
    {
        get => _deviceAliases;
        set => SetProperty(ref _deviceAliases, value);
    }

    public List<string> ManagedDeviceIds
    {
        get => _managedDeviceIds;
        set => SetProperty(ref _managedDeviceIds, value);
    }

    public bool HasCompletedFirstRun
    {
        get => _hasCompletedFirstRun;
        set => SetProperty(ref _hasCompletedFirstRun, value);
    }

    public void EnsureDefaults()
    {
        Hotkeys ??= new List<HotkeyBinding>();
        DeviceAliases ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ManagedDeviceIds ??= new List<string>();
        Popup ??= new PopupSettings();

        foreach (var hotkey in Hotkeys)
            hotkey.EnsureTargets();

        // 1.x shipped global "all mice" actions. 2.0 is deliberately
        // device-scoped, so drop those legacy entries instead of offering
        // a one-click takeover that no longer exists.
        Hotkeys.RemoveAll(h => h.Action is HotkeyAction.ToggleAllMice or HotkeyAction.TakeoverAllMice);
    }
}
