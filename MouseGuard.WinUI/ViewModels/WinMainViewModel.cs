using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MouseGuard.Helpers;
using MouseGuard.Models;
using MouseGuard.Native;
using MouseGuard.Services;
using MouseGuard.WinUI.Services;
using MouseGuard.WinUI.Views;

namespace MouseGuard.WinUI.ViewModels;

public sealed class WinMainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly DeviceService _deviceService;
    private readonly AutoStartService _autoStart;
    private readonly InputService _input;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<AppTheme> _applyTheme;
    private readonly Action _requestExit;
    private readonly List<string> _managedIds = new();
    private readonly HashSet<string> _offlineManaged = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer _infoTimer;
    private readonly DispatcherQueueTimer _deviceTimer;
    private bool _refreshing;
    private bool _infoOpen;
    private string _infoMessage = string.Empty;
    private InfoBarSeverity _infoSeverity = InfoBarSeverity.Informational;
    private bool _isBusy;
    private bool _isAdministrator;
    private string _filter = "all";
    private string _searchText = string.Empty;
    private string _autoStartStatus = string.Empty;
    private string _lastNoticeDevice = string.Empty;
    private int _mouseCount;
    private int _keyboardCount;

    public WinMainViewModel(
        SettingsService settingsService,
        DeviceService deviceService,
        AutoStartService autoStart,
        InputService input,
        DispatcherQueue dispatcher,
        Action<AppTheme> applyTheme,
        Action requestExit)
    {
        _settingsService = settingsService;
        _deviceService = deviceService;
        _autoStart = autoStart;
        _input = input;
        _dispatcher = dispatcher;
        _applyTheme = applyTheme;
        _requestExit = requestExit;

        Settings = settingsService.Settings;
        Settings.EnsureDefaults();
        _managedIds.AddRange(Settings.ManagedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase));
        _isAdministrator = SetupApiInterop.IsAdministrator;

        Devices = new ObservableCollection<DeviceItem>();
        VisibleDevices = new ObservableCollection<DeviceItem>();
        Hotkeys = new ObservableCollection<HotkeyBinding>(Settings.Hotkeys);
        Activity = new ObservableCollection<ActivityEntry>();

        _infoTimer = dispatcher.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromSeconds(4);
        _infoTimer.IsRepeating = false;
        _infoTimer.Tick += (_, _) => InfoOpen = false;

        _deviceTimer = dispatcher.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(5);
        _deviceTimer.IsRepeating = true;
        _deviceTimer.Tick += (_, _) => _ = RefreshDevicesAsync();

        _input.HotkeyTriggered += (_, binding) =>
            _dispatcher.TryEnqueue(() => _ = ExecuteHotkeyAsync(binding));
        _input.PanicReleaseRequested += (_, _) =>
            _dispatcher.TryEnqueue(() => _ = ReleaseAllAsync("紧急释放快捷键"));

        // WM_DEVICECHANGE normally covers USB receiver removal/insertion.
        // The timer below is a safety net for drivers that do not forward it.
        _input.DevicesChanged += (_, _) =>
            _dispatcher.TryEnqueue(() => _ = RefreshDevicesAsync());

        Settings.PropertyChanged += (_, _) => _settingsService.Save();
        Settings.Popup.PropertyChanged += (_, _) =>
        {
            _settingsService.Save();
            OnPropertyChanged(nameof(DeviceNameVisibility));
            OnPropertyChanged(nameof(FooterVisibility));
        };
    }

    public AppSettings Settings { get; }
    public IReadOnlyList<PopupPositionOption> PopupPositions { get; } = new[]
    {
        new PopupPositionOption(PopupPosition.Center, "屏幕中央", "最醒目，适合课堂投屏"),
        new PopupPositionOption(PopupPosition.TopRight, "右上角", "不遮挡主要内容"),
        new PopupPositionOption(PopupPosition.BottomRight, "右下角", "接近通知习惯位置")
    };

    public IReadOnlyList<AccentOption> PopupAccents { get; } = new[]
    {
        new AccentOption("Blue", "星蓝", "#60A5FA"),
        new AccentOption("Violet", "紫罗兰", "#A78BFA"),
        new AccentOption("Green", "薄荷", "#5BD6A6")
    };

    public PopupPositionOption SelectedPopupPosition
    {
        get => PopupPositions.FirstOrDefault(p => p.Value == Settings.Popup.Position) ?? PopupPositions[0];
        set
        {
            if (value is null || Settings.Popup.Position == value.Value)
                return;
            Settings.Popup.Position = value.Value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public AccentOption SelectedPopupAccent
    {
        get => PopupAccents.FirstOrDefault(a => a.Value == Settings.Popup.Accent) ?? PopupAccents[0];
        set
        {
            if (value is null || Settings.Popup.Accent == value.Value)
                return;
            Settings.Popup.Accent = value.Value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public ObservableCollection<DeviceItem> Devices { get; }
    public ObservableCollection<DeviceItem> VisibleDevices { get; }
    public ObservableCollection<HotkeyBinding> Hotkeys { get; }
    public ObservableCollection<ActivityEntry> Activity { get; }

    public bool IsAdministrator
    {
        get => _isAdministrator;
        private set
        {
            if (SetProperty(ref _isAdministrator, value))
            {
                OnPropertyChanged(nameof(PermissionLabel));
                OnPropertyChanged(nameof(ShowAdminInfo));
                OnPropertyChanged(nameof(AdminVisibility));
            }
        }
    }

    public string PermissionLabel => IsAdministrator ? "管理员权限" : "标准权限";
    public bool ShowAdminInfo => !IsAdministrator;

    public string StatusText => TakenOverCount == 0 ? "输入设备均可用" : $"{TakenOverCount} 台设备已接管";
    public string StatusDetail => TakenOverCount == 0
        ? "使用设备卡片上的开关单独接管；快捷键可同时控制多台设备。"
        : "被接管设备已暂时禁用。按紧急释放组合键可立即恢复全部设备。";
    public string StatusGlyph => TakenOverCount == 0 ? "\uE73E" : "\uE72E";
    public string EmergencyHotkeyText => "Ctrl + Alt + Shift + Esc";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Filter
    {
        get => _filter;
        private set => SetProperty(ref _filter, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                RebuildVisibleDevices();
        }
    }

    public int MouseCount
    {
        get => _mouseCount;
        private set => SetProperty(ref _mouseCount, value);
    }

    public int KeyboardCount
    {
        get => _keyboardCount;
        private set => SetProperty(ref _keyboardCount, value);
    }

    public int TakenOverCount => Devices.Count(d => d.IsTakenOver);
    public int HotkeyCount => Hotkeys.Count(h => h.IsEnabled);
    public bool HasHotkeys => Hotkeys.Count > 0;
    public Visibility HotkeyEmptyVisibility => HasHotkeys ? Visibility.Collapsed : Visibility.Visible;
    public string DeviceSummary => $"{MouseCount} 个鼠标 · {KeyboardCount} 个键盘";
    public string DataDirectory => _settingsService.DataDirectory;
    public string AppVersion => "2.0.0";

    public bool HasDevices => VisibleDevices.Count > 0;
    public bool IsEmpty => !HasDevices;
    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdminVisibility => ShowAdminInfo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeviceNameVisibility => Settings.Popup.ShowDeviceName ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FooterVisibility => Settings.Popup.ShowFooterBranding ? Visibility.Visible : Visibility.Collapsed;

    public string AutoStartStatus
    {
        get => _autoStartStatus;
        private set => SetProperty(ref _autoStartStatus, value);
    }

    public bool InfoOpen
    {
        get => _infoOpen;
        private set => SetProperty(ref _infoOpen, value);
    }

    public string InfoMessage
    {
        get => _infoMessage;
        private set => SetProperty(ref _infoMessage, value);
    }

    public InfoBarSeverity InfoSeverity
    {
        get => _infoSeverity;
        private set => SetProperty(ref _infoSeverity, value);
    }

    public bool LaunchAtStartup
    {
        get => Settings.LaunchAtStartup;
        set
        {
            if (Settings.LaunchAtStartup == value)
                return;
            Settings.LaunchAtStartup = value;
            OnPropertyChanged();
            _ = ApplyAutoStartAsync(value);
        }
    }

    public bool StartMinimized
    {
        get => Settings.StartMinimized;
        set
        {
            Settings.StartMinimized = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set
        {
            Settings.MinimizeToTray = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public bool ShowTakeoverPopup
    {
        get => Settings.ShowTakeoverPopup;
        set
        {
            Settings.ShowTakeoverPopup = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public bool UseSystemNotifications
    {
        get => Settings.UseSystemNotifications;
        set
        {
            Settings.UseSystemNotifications = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public bool AutoReleaseOnReconnect
    {
        get => Settings.AutoReleaseOnReconnect;
        set
        {
            Settings.AutoReleaseOnReconnect = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public async Task InitializeAsync()
    {
        _input.Start();
        _input.UpdateBindings(Hotkeys);
        AutoStartStatus = _autoStart.GetStatusText();
        _deviceTimer.Start();

        await RefreshDevicesAsync();
        await RecoverStaleDevicesAsync();
        await EnsureAutoStartAsync();

        if (_input.FailedHotkeys.Count > 0)
            ShowInfo("以下快捷键被系统或其他程序占用：" + string.Join("、", _input.FailedHotkeys),
                InfoBarSeverity.Warning);

        AddActivity("控制台已就绪", "设备开关与快捷键已启用", StatusTone.Success, "\uE73E");
    }

    public void SetFilter(string filter)
    {
        Filter = filter is "mouse" or "keyboard" ? filter : "all";
        RebuildVisibleDevices();
    }

    public async Task RefreshDevicesAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            var devices = await Task.Run(_deviceService.Refresh);
            var aliases = Settings.DeviceAliases;
            var presentIds = devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var reconnected = new List<InputDevice>();
            foreach (var id in _managedIds.ToList())
            {
                if (!presentIds.TryGetValue(id, out _))
                {
                    _offlineManaged.Add(id);
                    continue;
                }

                var device = devices.First(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (device.State == DeviceState.Unavailable)
                {
                    _offlineManaged.Add(id);
                }
                else if (device.State != DeviceState.Unavailable && _offlineManaged.Contains(id))
                {
                    reconnected.Add(device);
                }
            }

            Devices.Clear();
            foreach (var device in devices)
            {
                aliases.TryGetValue(device.Id, out var alias);
                var item = new DeviceItem(device, alias ?? string.Empty);
                // Do not carry over the old visual state. It may still say
                // "taken over" while the device is transiently reported as
                // disabled after a release. The managed list decides.
                if (_managedIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
                    item.IsTakenOver = true;
                Devices.Add(item);
            }

            MouseCount = Devices.Count(d => d.Kind == DeviceKind.Mouse);
            KeyboardCount = Devices.Count(d => d.Kind == DeviceKind.Keyboard);
            RebuildVisibleDevices();
            OnPropertyChanged(nameof(TakenOverCount));
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(StatusGlyph));

            if (Settings.AutoReleaseOnReconnect && reconnected.Count > 0)
                await AutoReleaseReconnectedAsync(reconnected);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描设备失败", ex);
            ShowInfo("扫描设备失败，请稍后重试。", InfoBarSeverity.Error);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public async Task SetDeviceTakenAsync(DeviceItem item, bool takeOver)
    {
        if (item.IsBusy || item.IsTakenOver == takeOver)
            return;

        if (takeOver)
            await TakeDeviceAsync(item);
        else
            await ReleaseDeviceAsync(item);
    }

    public async Task ToggleDeviceAsync(DeviceItem item)
        => await SetDeviceTakenAsync(item, !item.IsTakenOver);

    private async Task TakeDeviceAsync(DeviceItem item)
    {
        if (!IsAdministrator)
        {
            ShowInfo("接管单台设备需要管理员权限。", InfoBarSeverity.Warning);
            return;
        }

        if (!CanDisableDevice(item))
            return;

        item.IsBusy = true;
        try
        {
            var (success, error) = _deviceService.SetEnabled(item.InstanceId, item.Kind, false);
            if (!success)
            {
                ShowInfo("接管失败：" + error, InfoBarSeverity.Error);
                return;
            }

            item.IsTakenOver = true;
            if (!_managedIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                _managedIds.Add(item.Id);
            PersistManaged();

            _lastNoticeDevice = item.DisplayName;
            AddActivity("已接管" + item.KindLabel, item.DisplayName, StatusTone.Warning,
                item.Kind == DeviceKind.Mouse ? "\uE962" : "\uE765");
            ShowInfo($"{item.DisplayName} 已接管。按 {EmergencyHotkeyText} 可紧急释放。", InfoBarSeverity.Success);
            ShowPopupFor(new[] { item });
            await RefreshDevicesAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task ReleaseDeviceAsync(DeviceItem item)
    {
        if (!IsAdministrator)
        {
            ShowInfo("释放设备需要管理员权限。", InfoBarSeverity.Warning);
            return;
        }

        item.IsBusy = true;
        try
        {
            if (item.State == DeviceState.Disabled)
            {
                var (success, error) = _deviceService.SetEnabled(item.InstanceId, item.Kind, true);
                if (!success)
                {
                    ShowInfo("释放失败：" + error, InfoBarSeverity.Error);
                    return;
                }
            }

            item.IsTakenOver = false;
            _managedIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            _offlineManaged.Remove(item.Id);
            PersistManaged();
            AddActivity("已释放" + item.KindLabel, item.DisplayName, StatusTone.Success, "\uE785");
            ShowInfo($"{item.DisplayName} 已恢复。", InfoBarSeverity.Success);
            await RefreshDevicesAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private bool CanDisableDevice(DeviceItem item)
    {
        if (item.Kind != DeviceKind.Keyboard)
            return true;

        var activeKeyboards = Devices.Count(d =>
            d.Kind == DeviceKind.Keyboard && d.State == DeviceState.Active && !d.IsTakenOver);
        if (activeKeyboards > 1)
            return true;

        ShowInfo("至少保留一个键盘，避免无法使用紧急释放快捷键。", InfoBarSeverity.Warning);
        return false;
    }

    private async Task ToggleDevicesAsync(IReadOnlyList<DeviceItem> targets)
    {
        var toTake = targets.Where(t => !t.IsTakenOver).ToList();
        var toRelease = targets.Where(t => t.IsTakenOver).ToList();

        if (toTake.Count > 0)
            await TakeDevicesAsync(toTake);
        if (toRelease.Count > 0)
            await ReleaseDevicesAsync(toRelease, "快捷键");
    }

    private async Task TakeDevicesAsync(IReadOnlyList<DeviceItem> items)
    {
        if (!IsAdministrator)
        {
            ShowInfo("接管设备需要管理员权限。", InfoBarSeverity.Warning);
            return;
        }

        var taken = new List<DeviceItem>();
        foreach (var item in items)
        {
            if (item.IsBusy || item.IsTakenOver || !CanDisableDevice(item))
                continue;

            item.IsBusy = true;
            var (success, _) = _deviceService.SetEnabled(item.InstanceId, item.Kind, false);
            item.IsBusy = false;
            if (!success)
                continue;

            item.IsTakenOver = true;
            if (!_managedIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                _managedIds.Add(item.Id);
            taken.Add(item);
        }

        if (taken.Count == 0)
            return;

        PersistManaged();
        _lastNoticeDevice = taken[0].DisplayName;
        AddActivity($"已接管 {taken.Count} 台设备",
            string.Join("、", taken.Take(3).Select(t => t.DisplayName)), StatusTone.Warning,
            taken[0].Kind == DeviceKind.Mouse ? "\uE962" : "\uE765");
        ShowInfo($"已接管 {taken.Count} 台设备。按 {EmergencyHotkeyText} 可紧急释放。",
            InfoBarSeverity.Success);
        ShowPopupFor(taken);
        await RefreshDevicesAsync();
    }

    private async Task ReleaseDevicesAsync(IReadOnlyList<DeviceItem> items, string reason)
    {
        if (!IsAdministrator)
        {
            ShowInfo("释放设备需要管理员权限。", InfoBarSeverity.Warning);
            return;
        }

        foreach (var item in items)
        {
            if (item.State == DeviceState.Disabled)
                _deviceService.SetEnabled(item.InstanceId, item.Kind, true);

            item.IsTakenOver = false;
            _managedIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            _offlineManaged.Remove(item.Id);
        }

        PersistManaged();
        AddActivity("已释放所选设备",
            string.Join("、", items.Take(3).Select(i => i.DisplayName)) + $"（{reason}）",
            StatusTone.Success, "\uE785");
        ShowInfo("所选设备已恢复。", InfoBarSeverity.Success);
        await RefreshDevicesAsync();
    }

    public async Task ReleaseAllAsync(string reason)
    {
        foreach (var id in _managedIds.ToList())
        {
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device is not null && device.State == DeviceState.Disabled)
                _deviceService.SetEnabled(device.InstanceId, device.Kind, true);
        }

        foreach (var device in Devices)
            device.IsTakenOver = false;

        _managedIds.Clear();
        _offlineManaged.Clear();
        Settings.ManagedDeviceIds.Clear();
        _settingsService.Save();
        OnPropertyChanged(nameof(TakenOverCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        AddActivity("已紧急释放全部设备", reason, StatusTone.Success, "\uE785");
        ShowInfo("所有设备控制权已释放。", InfoBarSeverity.Success);
        await RefreshDevicesAsync();
    }

    private async Task AutoReleaseReconnectedAsync(IEnumerable<InputDevice> reconnected)
    {
        var names = new List<string>();
        foreach (var device in reconnected)
        {
            var item = Devices.FirstOrDefault(d => d.Id == device.Id);
            if (item is null)
                continue;

            if (item.State == DeviceState.Disabled)
                _deviceService.SetEnabled(item.InstanceId, item.Kind, true);

            item.IsTakenOver = false;
            _managedIds.RemoveAll(id => string.Equals(id, device.Id, StringComparison.OrdinalIgnoreCase));
            _offlineManaged.Remove(device.Id);
            names.Add(item.DisplayName);
        }

        if (names.Count == 0)
            return;

        PersistManaged();
        OnPropertyChanged(nameof(TakenOverCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        AddActivity("设备重新连接，已自动释放",
            string.Join("、", names.Take(3)), StatusTone.Success, "\uE785");
        ShowInfo("检测到接收器重新连接，已自动释放：" + string.Join("、", names.Take(2)),
            InfoBarSeverity.Informational);
        await Task.CompletedTask;
    }

    private void PersistManaged()
    {
        Settings.ManagedDeviceIds = _managedIds.ToList();
        _settingsService.Save();
    }

    private void ShowPopupFor(IReadOnlyList<DeviceItem> items)
    {
        if (items.Count == 0)
            return;

        var name = items.Count == 1
            ? items[0].DisplayName
            : $"{items[0].DisplayName} 等 {items.Count} 台设备";
        ShowTakeoverNotice(name, items[0].Kind);
    }

    public void ShowPopup(string? deviceName, DeviceKind kind)
        => ShowTakeoverNotice(deviceName, kind);

    private void ShowTakeoverNotice(string? deviceName, DeviceKind kind)
    {
        if (Settings.UseSystemNotifications)
        {
            var body = string.IsNullOrWhiteSpace(deviceName)
                ? Settings.Popup.Message
                : $"{Settings.Popup.Message}\n{deviceName}";
            if (NotificationService.Show(Settings.Popup.Title, body))
                return;
        }

        if (!Settings.ShowTakeoverPopup)
            return;

        var popup = new TakeoverWindow(Settings.Popup, kind, deviceName);
        popup.ShowNoActivate();
    }

    public void ShowTestPopup()
    {
        var name = Devices.FirstOrDefault(d => d.Kind == DeviceKind.Mouse)?.DisplayName ?? "已连接的鼠标";
        ShowTakeoverNotice(name, DeviceKind.Mouse);
    }

    public void ResetPopup()
    {
        var defaults = new PopupSettings();
        Settings.Popup.Title = defaults.Title;
        Settings.Popup.Message = defaults.Message;
        Settings.Popup.DurationSeconds = defaults.DurationSeconds;
        Settings.Popup.Position = defaults.Position;
        Settings.Popup.ShowDeviceName = defaults.ShowDeviceName;
        Settings.Popup.ShowFooterBranding = defaults.ShowFooterBranding;
        Settings.Popup.FooterText = defaults.FooterText;
        Settings.Popup.Accent = defaults.Accent;
        OnPropertyChanged(nameof(SelectedPopupPosition));
        OnPropertyChanged(nameof(SelectedPopupAccent));
        _settingsService.Save();
    }

    private async Task ExecuteHotkeyAsync(HotkeyBinding binding)
    {
        if (binding.Action == HotkeyAction.ReleaseAll)
        {
            await ReleaseAllAsync("快捷键 " + binding.GestureText);
            return;
        }

        if (binding.Action == HotkeyAction.ShowTakeoverNotice)
        {
            ShowPopup(string.IsNullOrWhiteSpace(_lastNoticeDevice) ? "已连接的设备" : _lastNoticeDevice,
                DeviceKind.Mouse);
            return;
        }

        var targets = ResolveTargets(binding);
        if (targets.Count == 0)
        {
            ShowInfo("快捷键绑定的设备当前未连接。", InfoBarSeverity.Warning);
            return;
        }

        switch (binding.Action)
        {
            case HotkeyAction.Takeover:
                await TakeDevicesAsync(targets);
                break;
            case HotkeyAction.Release:
                await ReleaseDevicesAsync(targets, "快捷键");
                break;
            case HotkeyAction.ToggleTakeover:
                await ToggleDevicesAsync(targets);
                break;
        }
    }

    private List<DeviceItem> ResolveTargets(HotkeyBinding binding)
    {
        var ids = binding.TargetDeviceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? new List<string>();
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(binding.TargetDeviceId))
            ids.Add(binding.TargetDeviceId);
        return Devices.Where(d => ids.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public void AddOrUpdateHotkey(HotkeyBinding binding, bool isNew)
    {
        binding.EnsureTargets();
        var existing = Hotkeys.FirstOrDefault(h => h.Id == binding.Id);
        if (existing is null)
        {
            Hotkeys.Add(binding);
            Settings.Hotkeys.Add(binding);
        }
        else
        {
            var index = Hotkeys.IndexOf(existing);
            Hotkeys[index] = binding;
            var settingsIndex = Settings.Hotkeys.FindIndex(h => h.Id == existing.Id);
            if (settingsIndex >= 0)
                Settings.Hotkeys[settingsIndex] = binding;
        }

        if (string.IsNullOrWhiteSpace(binding.Name))
            binding.Name = binding.ActionName;

        _input.UpdateBindings(Hotkeys);
        OnPropertyChanged(nameof(HotkeyCount));
        OnPropertyChanged(nameof(HasHotkeys));
        OnPropertyChanged(nameof(HotkeyEmptyVisibility));
        _settingsService.Save();
        ShowInfo(isNew ? "快捷键已创建。" : "快捷键已更新。", InfoBarSeverity.Success);
    }

    public void DeleteHotkey(HotkeyBinding binding)
    {
        Hotkeys.Remove(binding);
        Settings.Hotkeys.RemoveAll(h => h.Id == binding.Id);
        _input.UpdateBindings(Hotkeys);
        OnPropertyChanged(nameof(HotkeyCount));
        OnPropertyChanged(nameof(HasHotkeys));
        OnPropertyChanged(nameof(HotkeyEmptyVisibility));
        _settingsService.Save();
    }

    public void ToggleHotkey(HotkeyBinding binding)
    {
        _input.UpdateBindings(Hotkeys);
        _settingsService.Save();
        OnPropertyChanged(nameof(HotkeyCount));
    }

    public void ApplyDeviceAlias(DeviceItem item, string alias)
    {
        item.Alias = alias;
        if (string.IsNullOrWhiteSpace(alias))
            Settings.DeviceAliases.Remove(item.Id);
        else
            Settings.DeviceAliases[item.Id] = alias;
        _settingsService.Save();
        RebuildVisibleDevices();
    }

    public void SetTheme(AppTheme theme)
    {
        Settings.Theme = theme;
        _settingsService.Save();
        _applyTheme(theme);
    }

    private async Task ApplyAutoStartAsync(bool enable)
    {
        var (success, message) = enable ? _autoStart.Enable() : _autoStart.Disable();
        if (!success)
        {
            Settings.LaunchAtStartup = false;
            OnPropertyChanged(nameof(LaunchAtStartup));
            ShowInfo(message, InfoBarSeverity.Error);
        }
        else
        {
            AutoStartStatus = _autoStart.GetStatusText();
            ShowInfo(message, InfoBarSeverity.Success);
        }

        _settingsService.Save();
        await Task.CompletedTask;
    }

    private async Task EnsureAutoStartAsync()
    {
        try
        {
            if (Settings.LaunchAtStartup && !_autoStart.IsEnabled)
            {
                var (success, message) = _autoStart.Enable();
                if (!success)
                {
                    Settings.LaunchAtStartup = false;
                    OnPropertyChanged(nameof(LaunchAtStartup));
                    ShowInfo(message, InfoBarSeverity.Warning);
                }
            }
            else if (!Settings.LaunchAtStartup && _autoStart.IsEnabled)
            {
                _autoStart.Disable();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("初始化开机启动失败", ex);
        }

        AutoStartStatus = _autoStart.GetStatusText();
        await Task.CompletedTask;
    }

    private async Task RecoverStaleDevicesAsync()
    {
        if (_managedIds.Count == 0)
            return;

        if (IsAdministrator)
        {
            foreach (var id in _managedIds.ToList())
            {
                var device = Devices.FirstOrDefault(d => d.Id == id);
                if (device is null)
                {
                    _offlineManaged.Add(id);
                    continue;
                }

                if (device.State == DeviceState.Disabled)
                    _deviceService.SetEnabled(device.InstanceId, device.Kind, true);

                device.IsTakenOver = false;
                _managedIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            _managedIds.Clear();
            _offlineManaged.Clear();
        }

        PersistManaged();
        await RefreshDevicesAsync();
    }

    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + _settingsService.DataDirectory + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowInfo("无法打开数据目录：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    public void ResetAll()
    {
        Shutdown();
        _settingsService.Reset();
        _requestExit();
    }

    public void Elevate()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--replace",
                UseShellExecute = true,
                Verb = "runas"
            });
            _requestExit();
        }
        catch (Exception ex)
        {
            LogService.Error("提权失败", ex);
            ShowInfo("未能获取管理员权限。", InfoBarSeverity.Error);
        }
    }

    public void Shutdown()
    {
        _deviceTimer.Stop();
        foreach (var id in _managedIds.ToList())
        {
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device is not null && device.State == DeviceState.Disabled)
                _deviceService.SetEnabled(device.InstanceId, device.Kind, true);
        }

        _managedIds.Clear();
        _offlineManaged.Clear();
        Settings.ManagedDeviceIds.Clear();
        _settingsService.Save();
        _input.Dispose();
    }

    public void AddActivity(string text, string detail, StatusTone tone, string glyph)
    {
        Activity.Insert(0, new ActivityEntry { Text = text, Detail = detail, Tone = tone, Glyph = glyph });
        while (Activity.Count > 30)
            Activity.RemoveAt(Activity.Count - 1);
    }

    public void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoMessage = message;
        InfoSeverity = severity;
        InfoOpen = true;
        _infoTimer.Stop();
        _infoTimer.Start();
    }

    private void RebuildVisibleDevices()
    {
        IEnumerable<DeviceItem> query = Devices;
        if (Filter == "mouse")
            query = query.Where(d => d.Kind == DeviceKind.Mouse);
        else if (Filter == "keyboard")
            query = query.Where(d => d.Kind == DeviceKind.Keyboard);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(d =>
                d.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                d.Subtitle.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                d.DevicePath.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        VisibleDevices.Clear();
        foreach (var item in query)
            VisibleDevices.Add(item);

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyVisibility));
    }
}
