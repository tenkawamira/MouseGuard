using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MouseGuard.Helpers;
using MouseGuard.Dialogs;
using MouseGuard.Models;
using MouseGuard.Native;
using MouseGuard.Services;
using MouseGuard.Views;

namespace MouseGuard.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly DeviceService _deviceService;
    private readonly AutoStartService _autoStart;
    private readonly ThemeService _theme;
    private readonly RawInputService _rawInput;
    private readonly InputHookService _hooks;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _refreshDebounce;
    private readonly List<string> _managedDeviceIds = new();
    private bool _initialized;

    private string _deviceFilter = "all";
    private string _searchText = string.Empty;
    private NavigationItem _selectedNavigation;
    private bool _miceLocked;
    private bool _keyboardsLocked;
    private bool _isBusy;
    private bool _isAdministrator;
    private bool _toastVisible;
    private string _toastMessage = string.Empty;
    private StatusTone _toastTone = StatusTone.Accent;
    private int _mouseCount;
    private int _keyboardCount;
    private string _currentPageKey = "overview";
    private string _autoStartStatus = string.Empty;
    private string _lastNoticeDevice = string.Empty;

    public MainViewModel(
        SettingsService settingsService,
        DeviceService deviceService,
        AutoStartService autoStart,
        ThemeService theme,
        RawInputService rawInput,
        InputHookService hooks)
    {
        _settingsService = settingsService;
        _deviceService = deviceService;
        _autoStart = autoStart;
        _theme = theme;
        _rawInput = rawInput;
        _hooks = hooks;

        Settings = settingsService.Settings;
        Settings.EnsureDefaults();
        _isAdministrator = SetupApiInterop.IsAdministrator;
        _managedDeviceIds.AddRange(Settings.ManagedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase));

        Devices = new ObservableCollection<DeviceItem>();
        VisibleDevices = new ObservableCollection<DeviceItem>();
        Hotkeys = new ObservableCollection<HotkeyBinding>(Settings.Hotkeys);
        Activity = new ObservableCollection<ActivityEntry>();
        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new("overview", "概览", "\uE80F", "总览设备与快捷操作"),
            new("devices", "设备", "\uE772", "管理鼠标与键盘"),
            new("hotkeys", "快捷键", "\uE765", "绑定控制指令"),
            new("notifications", "提示", "\uE7E7", "自定义接管弹窗"),
            new("settings", "设置", "\uE713", "启动与权限")
        };

        _selectedNavigation = NavigationItems[0];
        _selectedNavigation.IsSelected = true;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastVisible = false;
        };

        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _refreshDebounce.Tick += async (_, _) =>
        {
            _refreshDebounce.Stop();
            await RefreshDevicesAsync(reconcileHardware: true);
        };

        foreach (var binding in Hotkeys)
            binding.PropertyChanged += HotkeyOnPropertyChanged;

        _hooks.UpdateBindings(Hotkeys);
        _hooks.HotkeyTriggered += HooksOnHotkeyTriggered;
        _hooks.PanicReleaseRequested += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(async () => await ReleaseAllAsync("紧急释放"));
        };
        _rawInput.DevicesChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            });
        };

        Settings.PropertyChanged += SettingsOnPropertyChanged;
        Settings.Popup.PropertyChanged += (_, e) =>
        {
            _settingsService.Save();
            if (e.PropertyName == nameof(PopupSettings.DurationSeconds))
                OnPropertyChanged(nameof(PopupDurationLabel));
        };

        AddHotkeyCommand = new RelayCommand(_ => RequestAddHotkey(null));
        RefreshDevicesCommand = new AsyncRelayCommand(() => RefreshDevicesAsync(true));
        SetDeviceFilterCommand = new RelayCommand(p => SetDeviceFilter(p?.ToString() ?? "all"));
        ToggleDeviceCommand = new AsyncRelayCommand(p => ToggleDeviceAsync(p as DeviceItem));
        BindDeviceHotkeyCommand = new RelayCommand(p => RequestAddHotkey(p as DeviceItem));
        ToggleMiceCommand = new AsyncRelayCommand(ToggleMiceAsync);
        ReleaseAllCommand = new AsyncRelayCommand(() => ReleaseAllAsync("手动释放"));
        ShowPopupCommand = new RelayCommand(() => ShowTakeoverPopup(_lastNoticeDevice, DeviceKind.Mouse, force: true));
        TestPopupCommand = new RelayCommand(() =>
        {
            var deviceName = Devices.FirstOrDefault(d => d.Kind == DeviceKind.Mouse)?.DisplayName ?? "已连接的鼠标";
            ShowTakeoverPopup(deviceName, DeviceKind.Mouse, force: true);
        });
        EditHotkeyCommand = new RelayCommand(p => { if (p is HotkeyBinding b) EditHotkeyRequested?.Invoke(this, b); });
        DeleteHotkeyCommand = new RelayCommand(p => DeleteHotkey(p as HotkeyBinding));
        ToggleHotkeyCommand = new RelayCommand(p => ToggleHotkey(p as HotkeyBinding));
        NavigateCommand = new RelayCommand(p => Navigate(p?.ToString() ?? "overview"));
        EditAutostartCommand = new AsyncRelayCommand(ToggleAutostartAsync);
        ElevateCommand = new AsyncRelayCommand(ElevateAsync);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        SetThemeCommand = new RelayCommand(p => SetTheme(p?.ToString()));
        RenameDeviceCommand = new RelayCommand(p => RenameDeviceRequested?.Invoke(this, p as DeviceItem));
        CopyDeviceIdCommand = new RelayCommand(p => CopyDeviceId(p as DeviceItem));
        ClearActivityCommand = new RelayCommand(() => Activity.Clear());
        SetPopupAccentCommand = new RelayCommand(p => SetPopupAccent(p?.ToString()));
        ResetPopupCommand = new RelayCommand(ResetPopup);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<DeviceItem> Devices { get; }
    public ObservableCollection<DeviceItem> VisibleDevices { get; }
    public ObservableCollection<HotkeyBinding> Hotkeys { get; }
    public ObservableCollection<ActivityEntry> Activity { get; }
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public IReadOnlyList<string> Filters { get; } = new[] { "all", "mouse", "keyboard" };

    public IReadOnlyList<PopupPositionOption> PopupPositions { get; } = new[]
    {
        new PopupPositionOption(PopupPosition.Center, "屏幕中央", "最醒目，适合课堂投屏"),
        new PopupPositionOption(PopupPosition.TopRight, "右上角", "不遮挡主要内容"),
        new PopupPositionOption(PopupPosition.BottomRight, "右下角", "接近通知习惯位置")
    };

    public IReadOnlyList<AccentOption> PopupAccents { get; } = new[]
    {
        new AccentOption("Blue", "星蓝", "#6D8DFF"),
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

    public string PopupAccent => Settings.Popup.Accent;

    public bool IsBlueAccent => PopupAccent == "Blue";
    public bool IsVioletAccent => PopupAccent == "Violet";
    public bool IsGreenAccent => PopupAccent == "Green";

    public string PopupDurationLabel => $"{Settings.Popup.DurationSeconds} 秒后自动关闭";

    public RelayCommand AddHotkeyCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public RelayCommand SetDeviceFilterCommand { get; }
    public AsyncRelayCommand ToggleDeviceCommand { get; }
    public RelayCommand BindDeviceHotkeyCommand { get; }
    public AsyncRelayCommand ToggleMiceCommand { get; }
    public AsyncRelayCommand ReleaseAllCommand { get; }
    public RelayCommand ShowPopupCommand { get; }
    public RelayCommand TestPopupCommand { get; }
    public RelayCommand EditHotkeyCommand { get; }
    public RelayCommand DeleteHotkeyCommand { get; }
    public RelayCommand ToggleHotkeyCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand EditAutostartCommand { get; }
    public AsyncRelayCommand ElevateCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand SetThemeCommand { get; }
    public RelayCommand RenameDeviceCommand { get; }
    public RelayCommand CopyDeviceIdCommand { get; }
    public RelayCommand ClearActivityCommand { get; }
    public RelayCommand SetPopupAccentCommand { get; }
    public RelayCommand ResetPopupCommand { get; }

    public event EventHandler<DeviceItem?>? AddHotkeyRequested;
    public event EventHandler<HotkeyBinding>? EditHotkeyRequested;
    public event EventHandler<DeviceItem?>? RenameDeviceRequested;
    public event EventHandler<string>? NavigationChanged;
    public event EventHandler? LockStateChanged;

    public string DeviceFilter
    {
        get => _deviceFilter;
        private set
        {
            if (SetProperty(ref _deviceFilter, value))
            {
                OnPropertyChanged(nameof(IsAllFilter));
                OnPropertyChanged(nameof(IsMouseFilter));
                OnPropertyChanged(nameof(IsKeyboardFilter));
                RebuildVisibleDevices();
            }
        }
    }

    public bool IsAllFilter => DeviceFilter == "all";
    public bool IsMouseFilter => DeviceFilter == "mouse";
    public bool IsKeyboardFilter => DeviceFilter == "keyboard";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                RebuildVisibleDevices();
        }
    }

    public string CurrentPageKey
    {
        get => _currentPageKey;
        private set => SetProperty(ref _currentPageKey, value);
    }

    public NavigationItem SelectedNavigation
    {
        get => _selectedNavigation;
        private set
        {
            if (SetProperty(ref _selectedNavigation, value))
                OnPropertyChanged(nameof(CurrentPageTitle));
        }
    }

    public string CurrentPageTitle => SelectedNavigation.Label;
    public string CurrentPageDescription => SelectedNavigation.Description;

    public bool MiceLocked
    {
        get => _miceLocked;
        private set
        {
            if (!SetProperty(ref _miceLocked, value))
                return;

            OnPropertyChanged(nameof(IsGlobalLockActive));
            OnPropertyChanged(nameof(GlobalLockLabel));
            OnPropertyChanged(nameof(GlobalLockDescription));
            OnPropertyChanged(nameof(HeroTitle));
            OnPropertyChanged(nameof(HeroDescription));
            OnPropertyChanged(nameof(HeroStatusLabel));
            OnPropertyChanged(nameof(HeroTone));
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(TakenOverCount));
            LockStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool KeyboardsLocked
    {
        get => _keyboardsLocked;
        private set => SetProperty(ref _keyboardsLocked, value);
    }

    public bool IsGlobalLockActive => MiceLocked || KeyboardsLocked;

    public string GlobalLockLabel => MiceLocked ? "释放全部鼠标" : "接管全部鼠标";
    public string GlobalLockDescription => MiceLocked
        ? "所有鼠标已被接管，键盘仍可正常使用。"
        : IsAdministrator
            ? "立即禁用全部鼠标设备；键盘和紧急释放快捷键仍然有效。"
            : "在软件层拦截所有鼠标输入，适合课堂演示或临时锁定。";

    public string HeroTitle => MiceLocked ? "鼠标控制权已接管" : "一切尽在掌控";
    public string HeroDescription => MiceLocked
        ? "所有鼠标输入已被拦截。按 Ctrl + Alt + R 或点击右侧按钮即可释放。"
        : "连接设备、绑定属于您的快捷键，在需要时安静地接管或释放控制权。";
    public string HeroStatusLabel => MiceLocked ? "接管中" : "运行正常";
    public StatusTone HeroTone => MiceLocked ? StatusTone.Warning : StatusTone.Success;
    public string StatusHeadline => MiceLocked ? "全局鼠标接管已开启" : "鼠标卫士正在后台运行";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsAdministrator
    {
        get => _isAdministrator;
        private set
        {
            if (SetProperty(ref _isAdministrator, value))
            {
                OnPropertyChanged(nameof(PermissionLabel));
                OnPropertyChanged(nameof(ShowAdminBanner));
                OnPropertyChanged(nameof(AdminBannerText));
            }
        }
    }

    public bool ShowAdminBanner => !IsAdministrator;
    public string PermissionLabel => IsAdministrator ? "管理员模式" : "标准权限";
    public string AdminBannerText => "当前为标准权限。接管单个鼠标或键盘需要管理员权限，全局软件接管仍可使用。";

    public string AutoStartStatus
    {
        get => _autoStartStatus;
        private set => SetProperty(ref _autoStartStatus, value);
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
            _ = ApplyAutostartAsync(value);
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

    public bool TakeoverPopupEnabled
    {
        get => Settings.ShowTakeoverPopup;
        set
        {
            Settings.ShowTakeoverPopup = value;
            OnPropertyChanged();
            _settingsService.Save();
        }
    }

    public int MouseCount
    {
        get => _mouseCount;
        private set
        {
            if (SetProperty(ref _mouseCount, value))
                OnPropertyChanged(nameof(DeviceSummary));
        }
    }

    public int KeyboardCount
    {
        get => _keyboardCount;
        private set
        {
            if (SetProperty(ref _keyboardCount, value))
                OnPropertyChanged(nameof(DeviceSummary));
        }
    }

    public int TakenOverCount => Devices.Count(d =>
        d.IsTakenOver ||
        (MiceLocked && d.Kind == DeviceKind.Mouse) ||
        (KeyboardsLocked && d.Kind == DeviceKind.Keyboard));
    public int ActiveDeviceCount => Devices.Count(d => d.IsActive);
    public int HotkeyCount => Hotkeys.Count(h => h.IsEnabled);
    public string DeviceSummary => $"{MouseCount} 个鼠标 · {KeyboardCount} 个键盘";
    public bool HasDevices => VisibleDevices.Count > 0;
    public bool IsDeviceListEmpty => !HasDevices;
    public string EmptyDeviceText => string.IsNullOrWhiteSpace(SearchText)
        ? "没有检测到输入设备"
        : $"没有与“{SearchText}”匹配的设备";

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public StatusTone ToastTone
    {
        get => _toastTone;
        private set
        {
            if (SetProperty(ref _toastTone, value))
                OnPropertyChanged(nameof(ToastGlyph));
        }
    }

    public string ToastGlyph => ToastTone switch
    {
        StatusTone.Warning => "\uE7BA",
        StatusTone.Success => "\uE73E",
        _ => "\uE946"
    };

    public string AppVersion
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string DataDirectory => _settingsService.DataDirectory;

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        AutoStartStatus = _autoStart.GetStatusText();
        await RefreshDevicesAsync(reconcileHardware: false);
        await RecoverStaleDevicesAsync();
        await EnsureAutostartAsync();

        var hooksStarted = _hooks.Start();
        LogService.Info("输入钩子启动: " + hooksStarted);
        if (!hooksStarted)
        {
            LogService.Warn("输入钩子未能完整启用。");
            ShowToast("全局快捷键未能启用，请尝试以管理员身份运行。", StatusTone.Warning);
        }

        _hooks.UpdateBindings(Hotkeys);
        if (_hooks.FailedHotkeys.Count > 0)
        {
            ShowToast("以下快捷键被系统或其他程序占用：" + string.Join("、", _hooks.FailedHotkeys), StatusTone.Warning);
        }
        AddActivity("控制台已就绪", "正在后台运行", StatusTone.Success, "\uE73E");

        if (!Settings.HasCompletedFirstRun)
        {
            Settings.HasCompletedFirstRun = true;
            _settingsService.Save();
            ShowToast("鼠标卫士已驻留托盘，可在设置中查看开机启动。", StatusTone.Accent);
        }
    }

    public void Navigate(string key)
    {
        var item = NavigationItems.FirstOrDefault(n => n.Key == key);
        if (item is null)
            return;

        foreach (var nav in NavigationItems)
            nav.IsSelected = nav == item;

        SelectedNavigation = item;
        CurrentPageKey = key;
        LogService.Info("Navigate: " + key);
        NavigationChanged?.Invoke(this, key);
    }

    public void SetDeviceFilter(string filter)
    {
        if (filter is not ("all" or "mouse" or "keyboard"))
            filter = "all";
        DeviceFilter = filter;
    }

    public async Task RefreshDevicesAsync(bool reconcileHardware = false)
    {
        if (IsBusy && reconcileHardware)
            return;

        try
        {
            var devices = await Task.Run(_deviceService.Refresh);
            var old = Devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var aliases = Settings.DeviceAliases;

            Devices.Clear();
            foreach (var device in devices)
            {
                aliases.TryGetValue(device.Id, out var alias);
                var item = new DeviceItem(device, alias ?? string.Empty);
                if (old.TryGetValue(device.Id, out var previous) && previous.IsTakenOver)
                    item.IsTakenOver = true;
                if (_managedDeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase))
                    item.IsTakenOver = true;
                Devices.Add(item);
            }

            MouseCount = Devices.Count(d => d.Kind == DeviceKind.Mouse);
            KeyboardCount = Devices.Count(d => d.Kind == DeviceKind.Keyboard);

            foreach (var binding in Hotkeys)
            {
                var target = Devices.FirstOrDefault(d => d.Id == binding.TargetDeviceId);
                if (target is not null)
                    binding.TargetDeviceName = target.DisplayName;

                var source = Devices.FirstOrDefault(d => d.Id == binding.SourceDeviceId);
                if (source is not null)
                    binding.SourceDeviceName = source.DisplayName;
            }

            RebuildVisibleDevices();
            OnPropertyChanged(nameof(TakenOverCount));
            OnPropertyChanged(nameof(ActiveDeviceCount));
            OnPropertyChanged(nameof(DeviceSummary));

            if (reconcileHardware && IsAdministrator)
                await ReconcileHardwareStateAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("扫描设备失败", ex);
            ShowToast("扫描设备失败，请稍后重试。", StatusTone.Warning);
        }
    }

    private async Task ReconcileHardwareStateAsync()
    {
        foreach (var item in Devices.Where(d => d.State == DeviceState.Disabled).ToList())
        {
            if (!_managedDeviceIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                item.IsTakenOver = true;
        }

        await Task.CompletedTask;
    }

    private async Task RecoverStaleDevicesAsync()
    {
        if (_managedDeviceIds.Count == 0)
            return;

        if (!IsAdministrator)
        {
            ShowToast("上次退出时仍有设备处于接管状态，请以管理员身份释放。", StatusTone.Warning);
            return;
        }

        foreach (var id in _managedDeviceIds.ToList())
        {
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device is null)
                continue;

            var (success, _) = _deviceService.SetEnabled(device.InstanceId, device.Kind, true);
            if (success)
                LogService.Info($"启动恢复：已释放 {device.DisplayName}");
        }

        _managedDeviceIds.Clear();
        Settings.ManagedDeviceIds.Clear();
        _settingsService.Save();
        await RefreshDevicesAsync();
    }

    private async Task ToggleDeviceAsync(DeviceItem? item)
    {
        if (item is null || item.IsBusy)
            return;

        if (MiceLocked && item.Kind == DeviceKind.Mouse)
        {
            ShowToast("请先释放“全部鼠标”接管，再单独管理这只鼠标。", StatusTone.Warning);
            return;
        }

        if (item.IsTakenOver)
            await ReleaseDeviceAsync(item);
        else
            await TakeDeviceAsync(item, showPopup: true);
    }

    private async Task TakeDeviceAsync(DeviceItem item, bool showPopup)
    {
        if (!IsAdministrator)
        {
            ShowToast("接管单个设备需要管理员权限，请点击“以管理员身份重启”。", StatusTone.Warning);
            return;
        }

        if (item.Kind == DeviceKind.Keyboard)
        {
            var activeKeyboards = Devices.Count(d => d.Kind == DeviceKind.Keyboard && d.State == DeviceState.Active);
            if (activeKeyboards <= 1)
            {
                ShowToast("至少保留一个键盘，避免无法使用紧急释放快捷键。", StatusTone.Warning);
                return;
            }
        }

        item.IsBusy = true;
        try
        {
            var (success, error) = _deviceService.SetEnabled(item.InstanceId, item.Kind, false);
            if (!success)
            {
                ShowToast("接管失败：" + error, StatusTone.Warning);
                return;
            }

            item.IsTakenOver = true;
            if (!_managedDeviceIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
                _managedDeviceIds.Add(item.Id);
            Settings.ManagedDeviceIds = _managedDeviceIds.ToList();
            _settingsService.Save();

            _lastNoticeDevice = item.DisplayName;
            AddActivity("已接管" + item.KindLabel, item.DisplayName, StatusTone.Warning, item.Kind == DeviceKind.Mouse ? "\uE962" : "\uE765");
            ShowToast($"{item.DisplayName} 已接管，按 Ctrl+Alt+Shift+Esc 可紧急释放。", StatusTone.Warning);
            OnPropertyChanged(nameof(TakenOverCount));

            if (showPopup && Settings.ShowTakeoverPopup)
                ShowTakeoverPopup(item.DisplayName, item.Kind);

            await RefreshDevicesAsync(reconcileHardware: true);
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
            ShowToast("释放设备需要管理员权限。", StatusTone.Warning);
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
                    ShowToast("释放失败：" + error, StatusTone.Warning);
                    return;
                }
            }

            item.IsTakenOver = false;
            _managedDeviceIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            Settings.ManagedDeviceIds = _managedDeviceIds.ToList();
            _settingsService.Save();

            AddActivity("已释放" + item.KindLabel, item.DisplayName, StatusTone.Success, "\uE785");
            ShowToast($"{item.DisplayName} 已恢复控制。", StatusTone.Success);
            OnPropertyChanged(nameof(TakenOverCount));
            await RefreshDevicesAsync(reconcileHardware: true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task ToggleMiceAsync()
    {
        if (!MiceLocked)
        {
            if (IsAdministrator)
                await SetAllMiceHardwareEnabledAsync(false);

            MiceLocked = true;
            _hooks.MiceBlocked = true;
            LogService.Info("全局鼠标接管开启");
            _lastNoticeDevice = "所有鼠标";
            AddActivity("已接管全部鼠标",
                IsAdministrator ? "设备级禁用已生效" : "软件层拦截已开启", StatusTone.Warning, "\uE962");
            ShowToast(IsAdministrator
                ? "全部鼠标已禁用，按 Ctrl+Alt+Shift+R 释放。"
                : "全部鼠标已接管，按 Ctrl+Alt+Shift+R 释放。", StatusTone.Warning);
            if (Settings.ShowTakeoverPopup)
                ShowTakeoverPopup("所有已连接的鼠标", DeviceKind.Mouse);

            await RefreshDevicesAsync(reconcileHardware: true);
        }
        else
        {
            MiceLocked = false;
            _hooks.MiceBlocked = false;
            LogService.Info("全局鼠标接管关闭");
            if (IsAdministrator)
                await SetAllMiceHardwareEnabledAsync(true);
            AddActivity("已释放全部鼠标", "设备与软件层均已恢复", StatusTone.Success, "\uE785");
            ShowToast("鼠标控制权已恢复。", StatusTone.Success);
            await RefreshDevicesAsync(reconcileHardware: true);
        }
    }

    private async Task SetAllMiceHardwareEnabledAsync(bool enabled)
    {
        foreach (var mouse in Devices.Where(d => d.Kind == DeviceKind.Mouse).ToList())
        {
            if (enabled && !_managedDeviceIds.Contains(mouse.Id, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!enabled && mouse.State != DeviceState.Active)
                continue;

            var (success, _) = _deviceService.SetEnabled(mouse.InstanceId, mouse.Kind, enabled);
            if (!success)
                continue;

            if (enabled)
            {
                _managedDeviceIds.RemoveAll(id => string.Equals(id, mouse.Id, StringComparison.OrdinalIgnoreCase));
                mouse.IsTakenOver = false;
            }
            else
            {
                if (!_managedDeviceIds.Contains(mouse.Id, StringComparer.OrdinalIgnoreCase))
                    _managedDeviceIds.Add(mouse.Id);
                mouse.IsTakenOver = true;
            }
        }

        Settings.ManagedDeviceIds = _managedDeviceIds.ToList();
        _settingsService.Save();
        await Task.CompletedTask;
    }

    public async Task ReleaseAllAsync(string reason)
    {
        LogService.Info("ReleaseAll: " + reason);
        _hooks.MiceBlocked = false;
        _hooks.KeyboardsBlocked = false;
        MiceLocked = false;
        KeyboardsLocked = false;

        var released = 0;
        foreach (var id in _managedDeviceIds.ToList())
        {
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device is null)
                continue;
            var (success, _) = _deviceService.SetEnabled(device.InstanceId, device.Kind, true);
            if (success)
                released++;
        }

        _managedDeviceIds.Clear();
        Settings.ManagedDeviceIds.Clear();
        _settingsService.Save();

        foreach (var item in Devices)
            item.IsTakenOver = false;

        OnPropertyChanged(nameof(TakenOverCount));
        AddActivity("全部控制已释放", string.IsNullOrWhiteSpace(reason) ? "所有接管状态已清除" : reason, StatusTone.Success, "\uE785");
        ShowToast("全部控制已释放。", StatusTone.Success);
        await RefreshDevicesAsync(reconcileHardware: true);
    }

    public void ShowTakeoverPopup(string? deviceName, DeviceKind kind, bool force = false, string? body = null)
    {
        if (!force && !Settings.ShowTakeoverPopup)
            return;

        try
        {
            var title = kind == DeviceKind.Mouse ? "鼠标已被接管" : "键盘已被接管";
            LogService.Info($"ShowTakeoverPopup: {title} / {deviceName}");
            var popup = new TakeoverPopupWindow(Settings.Popup, title, body, deviceName, kind);
            popup.Show();
            LogService.Info($"ShowTakeoverPopup shown: visible={popup.IsVisible}, opacity={popup.Opacity}");
        }
        catch (Exception ex)
        {
            LogService.Error("显示接管提示失败", ex);
        }
    }

    private void RequestAddHotkey(DeviceItem? device)
        => AddHotkeyRequested?.Invoke(this, device);

    public void AddOrUpdateHotkey(HotkeyBinding binding, bool isNew)
    {
        var existing = Hotkeys.FirstOrDefault(h => h.Id == binding.Id);
        if (existing is null)
        {
            binding.PropertyChanged += HotkeyOnPropertyChanged;
            Hotkeys.Add(binding);
            Settings.Hotkeys.Add(binding);
        }
        else
        {
            var index = Hotkeys.IndexOf(existing);
            existing.PropertyChanged -= HotkeyOnPropertyChanged;
            binding.PropertyChanged += HotkeyOnPropertyChanged;
            Hotkeys[index] = binding;
            var settingsIndex = Settings.Hotkeys.FindIndex(h => h.Id == existing.Id);
            if (settingsIndex >= 0)
                Settings.Hotkeys[settingsIndex] = binding;
        }

        if (string.IsNullOrWhiteSpace(binding.Name))
            binding.Name = binding.ActionName;

        _hooks.UpdateBindings(Hotkeys);
        OnPropertyChanged(nameof(HotkeyCount));
        _settingsService.Save();
        ShowToast(isNew ? "快捷键已创建。" : "快捷键已更新。", StatusTone.Success);
        AddActivity(isNew ? "新建快捷键" : "更新快捷键", $"{binding.GestureText} · {binding.ActionName}", StatusTone.Accent, "\uE765");
    }

    private void DeleteHotkey(HotkeyBinding? binding)
    {
        if (binding is null || binding.IsBuiltIn)
            return;

        binding.PropertyChanged -= HotkeyOnPropertyChanged;
        Hotkeys.Remove(binding);
        Settings.Hotkeys.RemoveAll(h => h.Id == binding.Id);
        _hooks.UpdateBindings(Hotkeys);
        OnPropertyChanged(nameof(HotkeyCount));
        _settingsService.Save();
        ShowToast("快捷键已删除。", StatusTone.Accent);
    }

    private void ToggleHotkey(HotkeyBinding? binding)
    {
        if (binding is null)
            return;
        binding.IsEnabled = !binding.IsEnabled;
        _hooks.UpdateBindings(Hotkeys);
        OnPropertyChanged(nameof(HotkeyCount));
        _settingsService.Save();
    }

    private void HotkeyOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _hooks.UpdateBindings(Hotkeys);
        _settingsService.Save();
        OnPropertyChanged(nameof(HotkeyCount));
    }

    private void HooksOnHotkeyTriggered(object? sender, InputHookService.HotkeyTriggeredEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await ExecuteHotkeyAsync(e.Binding);
        }));
    }

    private async Task ExecuteHotkeyAsync(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case HotkeyAction.ToggleAllMice:
                await ToggleMiceAsync();
                break;
            case HotkeyAction.TakeoverAllMice:
                if (!MiceLocked)
                    await ToggleMiceAsync();
                else if (Settings.ShowTakeoverPopup)
                    ShowTakeoverPopup("所有已连接的鼠标", DeviceKind.Mouse);
                break;
            case HotkeyAction.ReleaseAll:
                await ReleaseAllAsync("快捷键 " + binding.GestureText);
                break;
            case HotkeyAction.ShowTakeoverNotice:
                ShowTakeoverPopup(string.IsNullOrWhiteSpace(_lastNoticeDevice) ? "已连接的鼠标" : _lastNoticeDevice,
                    DeviceKind.Mouse, force: true);
                AddActivity("显示接管提示", "由快捷键 " + binding.GestureText + " 触发", StatusTone.Accent, "\uE7E7");
                break;
            default:
            {
                var target = Devices.FirstOrDefault(d => d.Id == binding.TargetDeviceId);
                if (target is null)
                {
                    ShowToast("快捷键绑定的目标设备已断开。", StatusTone.Warning);
                    return;
                }

                switch (binding.Action)
                {
                    case HotkeyAction.Takeover when !target.IsTakenOver:
                        await TakeDeviceAsync(target, showPopup: true);
                        break;
                    case HotkeyAction.Release when target.IsTakenOver:
                        await ReleaseDeviceAsync(target);
                        break;
                    case HotkeyAction.ToggleTakeover:
                        await ToggleDeviceAsync(target);
                        break;
                }

                break;
            }
        }
    }

    private async Task ToggleAutostartAsync()
    {
        LaunchAtStartup = !LaunchAtStartup;
        await Task.CompletedTask;
    }

    private async Task ApplyAutostartAsync(bool enable)
    {
        var (success, message) = enable ? _autoStart.Enable() : _autoStart.Disable();
        if (!success)
        {
            Settings.LaunchAtStartup = false;
            _settingsService.Save();
            AutoStartStatus = _autoStart.GetStatusText();
            OnPropertyChanged(nameof(LaunchAtStartup));
            ShowToast(message, StatusTone.Warning);
            return;
        }

        AutoStartStatus = _autoStart.GetStatusText();
        Settings.LaunchAtStartup = enable;
        _settingsService.Save();
        AddActivity(enable ? "已启用开机启动" : "已关闭开机启动", message, StatusTone.Success, "\uE7E8");
        ShowToast(message, StatusTone.Success);
        await Task.CompletedTask;
    }

    private async Task EnsureAutostartAsync()
    {
        try
        {
            var needsUpgrade = _autoStart.IsElevated && !_autoStart.IsTaskInstalled();
            if (Settings.LaunchAtStartup && (!_autoStart.IsEnabled || needsUpgrade))
            {
                var (success, message) = _autoStart.Enable();
                LogService.Info("初始化开机启动：" + message);
                if (!success)
                {
                    Settings.LaunchAtStartup = false;
                    _settingsService.Save();
                    OnPropertyChanged(nameof(LaunchAtStartup));
                    ShowToast(message, StatusTone.Warning);
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

    private async Task ElevateAsync()
    {
        try
        {
            await ReleaseAllAsync("切换到管理员模式");
            _settingsService.Save();
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
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            LogService.Error("请求管理员权限失败", ex);
            ShowToast("未能获取管理员权限，操作已取消。", StatusTone.Warning);
        }
    }

    private void SetPopupAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        Settings.Popup.Accent = value;
        OnPropertyChanged(nameof(PopupAccent));
        OnPropertyChanged(nameof(IsBlueAccent));
        OnPropertyChanged(nameof(IsVioletAccent));
        OnPropertyChanged(nameof(IsGreenAccent));
        _settingsService.Save();
    }

    private void ResetPopup()
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
        OnPropertyChanged(nameof(PopupAccent));
        OnPropertyChanged(nameof(IsBlueAccent));
        OnPropertyChanged(nameof(IsVioletAccent));
        OnPropertyChanged(nameof(IsGreenAccent));
        OnPropertyChanged(nameof(PopupDurationLabel));
        _settingsService.Save();
        ShowToast("提示样式已恢复默认。", StatusTone.Success);
    }

    private void SetTheme(string? value)
    {
        if (!Enum.TryParse<AppTheme>(value, true, out var theme))
            return;

        Settings.Theme = theme;
        _theme.Apply(theme);
        _settingsService.Save();
        OnPropertyChanged(nameof(SelectedThemeLabel));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsDarkEffective));
    }

    public bool IsDarkEffective => _theme.IsDark;

    public string SelectedThemeLabel => Settings.Theme switch
    {
        AppTheme.Light => "浅色",
        AppTheme.System => "跟随系统",
        _ => "深色"
    };

    public bool IsDarkTheme => Settings.Theme == AppTheme.Dark;
    public bool IsLightTheme => Settings.Theme == AppTheme.Light;
    public bool IsSystemTheme => Settings.Theme == AppTheme.System;

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_settingsService.DataDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast("无法打开数据目录：" + ex.Message, StatusTone.Warning);
        }
    }

    private void ResetSettings()
    {
        var dialog = new ConfirmWindow("恢复默认设置",
            "当前快捷键、设备别名和提示样式将被清除。已接管的设备会先被释放。", "恢复默认")
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true)
            return;

        Shutdown();
        _settingsService.Reset();
        Application.Current?.Shutdown();
    }

    private void CopyDeviceId(DeviceItem? item)
    {
        if (item is null)
            return;
        try
        {
            Clipboard.SetText(item.InstanceId);
            ShowToast("设备路径已复制。", StatusTone.Accent);
        }
        catch
        {
        }
    }

    private void RenameDevice(DeviceItem item, string alias)
    {
        item.Alias = alias;
        if (string.IsNullOrWhiteSpace(alias))
            Settings.DeviceAliases.Remove(item.Id);
        else
            Settings.DeviceAliases[item.Id] = alias;
        _settingsService.Save();
        RebuildVisibleDevices();
    }

    public void ApplyDeviceAlias(DeviceItem item, string alias) => RenameDevice(item, alias);

    private void RebuildVisibleDevices()
    {
        IEnumerable<DeviceItem> query = Devices;
        if (DeviceFilter == "mouse")
            query = query.Where(d => d.Kind == DeviceKind.Mouse);
        else if (DeviceFilter == "keyboard")
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
        OnPropertyChanged(nameof(IsDeviceListEmpty));
        OnPropertyChanged(nameof(EmptyDeviceText));
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _settingsService.Save();
        if (e.PropertyName is nameof(AppSettings.Theme))
        {
            OnPropertyChanged(nameof(SelectedThemeLabel));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsSystemTheme));
        }
    }

    private void AddActivity(string text, string detail, StatusTone tone, string glyph)
    {
        Activity.Insert(0, new ActivityEntry
        {
            Text = text,
            Detail = detail,
            Tone = tone,
            Glyph = glyph
        });

        while (Activity.Count > 40)
            Activity.RemoveAt(Activity.Count - 1);
    }

    public void ShowToast(string message, StatusTone tone)
    {
        ToastMessage = message;
        ToastTone = tone;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void DismissToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
    }

    /// <summary>
    /// Best-effort synchronous cleanup used while Windows is shutting down.
    /// Never leaves a device disabled after the application exits.
    /// </summary>
    public void Shutdown()
    {
        try
        {
            _hooks.MiceBlocked = false;
            _hooks.KeyboardsBlocked = false;

            foreach (var id in _managedDeviceIds.ToList())
            {
                var device = Devices.FirstOrDefault(d => d.Id == id);
                if (device is null)
                    continue;

                _deviceService.SetEnabled(device.InstanceId, device.Kind, true);
            }

            _managedDeviceIds.Clear();
            Settings.ManagedDeviceIds.Clear();
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            LogService.Error("退出清理失败", ex);
        }
    }
}
