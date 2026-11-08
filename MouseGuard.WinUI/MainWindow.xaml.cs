using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MouseGuard.Models;
using MouseGuard.Services;
using MouseGuard.WinUI.Services;
using MouseGuard.WinUI.ViewModels;
using MouseGuard.WinUI.Views;
using WinRT.Interop;

namespace MouseGuard.WinUI;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DeviceService _deviceService;
    private readonly AutoStartService _autoStart;
    private readonly bool _startInTray;
    private readonly string? _startPage;
    private readonly bool _previewPopup;
    private readonly InputService _input;
    private readonly TrayService _tray;
    private readonly Dictionary<string, Page> _pages = new();
    private readonly WinMainViewModel _viewModel;
    private bool _allowClose;
    private bool _disposed;

    public MainWindow(SettingsService settings, DeviceService devices, AutoStartService autoStart,
        bool startInTray, string? startPage = null, bool previewPopup = false)
    {
        InitializeComponent();
        _settingsService = settings;
        _deviceService = devices;
        _autoStart = autoStart;
        _startInTray = startInTray;
        _startPage = startPage;
        _previewPopup = previewPopup;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        ApplyBackdrop();

        var hwnd = WindowNative.GetWindowHandle(this);
        _input = new InputService(hwnd, devices.ResolveRawPath, DispatcherQueue);
        _tray = new TrayService(hwnd);
        _viewModel = new WinMainViewModel(settings, devices, autoStart, _input, DispatcherQueue,
            ApplyTheme, RequestExit);
        RootGrid.DataContext = _viewModel;

        _tray.OpenRequested += (_, _) => ShowFromTray();
        _tray.ReleaseAllRequested += (_, _) => _ = _viewModel.ReleaseAllAsync("托盘操作");
        _tray.ShowPopupRequested += (_, _) => _viewModel.ShowTestPopup();
        _tray.ExitRequested += (_, _) => RequestExit();
        NotificationService.BalloonFallback = (title, message) => _tray.ShowBalloon(title, message);
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(WinMainViewModel.IsAdministrator) or nameof(WinMainViewModel.PermissionLabel))
                PermissionItem.Content = _viewModel.PermissionLabel;
        };

        _pages["overview"] = new OverviewPage(_viewModel);
        _pages["devices"] = new DevicesPage(_viewModel);
        _pages["hotkeys"] = new HotkeysPage(_viewModel);
        _pages["notifications"] = new NotificationsPage(_viewModel);
        _pages["settings"] = new SettingsPage(_viewModel);

        ApplyTheme(settings.Settings.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        ApplyTitleBarColors();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("overview");

        AppWindow.Closing += OnClosing;
        Closed += (_, _) => Dispose();
    }

    public WinMainViewModel ViewModel => _viewModel;

    public async void InitializeAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
            PermissionItem.Content = _viewModel.PermissionLabel;
            ApplyTitleBarColors();
            if (!string.IsNullOrWhiteSpace(_startPage))
                Navigate(_startPage);
            if (_previewPopup)
                _viewModel.ShowTestPopup();
            if (_startInTray)
                AppWindow.Hide();
            else
                ShowFromTray();
        }
        catch (Exception ex)
        {
            LogService.Error("InitializeAsync 失败", ex);
            GlobalInfo.Message = "初始化失败：" + ex.Message;
            GlobalInfo.Severity = InfoBarSeverity.Error;
            GlobalInfo.IsOpen = true;
        }
    }

    public void Navigate(string key)
    {
        var item = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            Navigation.SelectedItem = item;
    }

    public void ShowFromTray()
    {
        if (_disposed)
            return;

        AppWindow.Show();
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string key)
            ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
            return;
        PageHost.Content = page;
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        var width = (int)(1180 * scale);
        var height = (int)(760 * scale);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        width = Math.Min(width, work.Width - 24);
        height = Math.Min(height, work.Height - 24);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2));

        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
    }

    private void ApplyTitleBarColors()
    {
        if (AppWindow.TitleBar is not { } titleBar)
            return;

        var light = RootGrid.ActualTheme == ElementTheme.Light;
        var foreground = light
            ? Windows.UI.Color.FromArgb(255, 24, 24, 24)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var inactiveForeground = light
            ? Windows.UI.Color.FromArgb(255, 110, 110, 110)
            : Windows.UI.Color.FromArgb(255, 176, 176, 176);
        var hoverBackground = light
            ? Windows.UI.Color.FromArgb(22, 0, 0, 0)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);
        var pressedBackground = light
            ? Windows.UI.Color.FromArgb(38, 0, 0, 0)
            : Windows.UI.Color.FromArgb(58, 255, 255, 255);

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }

    private void ApplyBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
            try
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };
        ApplyTitleBarColors();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        if (!_viewModel.MinimizeToTray)
        {
            _allowClose = true;
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
        _tray.ShowBalloon("鼠标卫士仍在运行", "快捷键继续生效；双击托盘图标可重新打开控制台。");
    }

    private void RequestExit()
    {
        _allowClose = true;
        _viewModel.Shutdown();
        _input.Dispose();
        _tray.Dispose();
        AppWindow.Destroy();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _viewModel.Shutdown();
            _input.Dispose();
            _tray.Dispose();
        }
        catch
        {
        }

        _settingsService.Save();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

