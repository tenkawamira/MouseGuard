using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MouseGuard.Dialogs;
using MouseGuard.Models;
using MouseGuard.Native;
using MouseGuard.ViewModels;
using MouseGuard.Views;

namespace MouseGuard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<string, UserControl> _pages = new();
    private bool _allowClose;
    private bool _syncingNavigation;
    private bool _trayTipShown;

    public event EventHandler? HiddenToTray;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _pages["overview"] = new OverviewView();
        _pages["devices"] = new DevicesView();
        _pages["hotkeys"] = new HotkeysView();
        _pages["notifications"] = new NotificationsView();
        _pages["settings"] = new SettingsView();

        NavigationList.SelectedIndex = 0;
        ShowPage("overview", animate: false);

        _viewModel.NavigationChanged += (_, key) =>
        {
            _syncingNavigation = true;
            var item = _viewModel.NavigationItems.FirstOrDefault(n => n.Key == key);
            if (item is not null)
                NavigationList.SelectedItem = item;
            _syncingNavigation = false;
            ShowPage(key, animate: true);
        };

        _viewModel.AddHotkeyRequested += OnAddHotkeyRequested;
        _viewModel.EditHotkeyRequested += OnEditHotkeyRequested;
        _viewModel.RenameDeviceRequested += OnRenameDeviceRequested;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    public void PrepareHidden()
    {
        ShowInTaskbar = false;
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        Hide();
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
            Show();

        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ShowPage(string key, bool animate)
    {
        if (!_pages.TryGetValue(key, out var page))
            return;

        if (PageHost.Children.Count == 1 && ReferenceEquals(PageHost.Children[0], page))
            return;

        PageHost.Children.Clear();
        PageHost.Children.Add(page);

        if (animate)
        {
            page.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            page.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            page.Opacity = 1;
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDarkEffective))
            ApplyWindowTheme();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme();
    }

    private void ApplyWindowTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.ApplyWindowStyle(hwnd, _viewModel.IsDarkEffective, roundCorners: true);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            HideToTray(notify: true);

        var maximized = WindowState == WindowState.Maximized;
        if (MaximizeGlyph is not null)
            MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        if (RestoreGlyph is not null)
            RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        HideToTray(notify: true);
    }

    private void HideToTray(bool notify)
    {
        Hide();
        ShowInTaskbar = false;
        if (!notify || _trayTipShown)
            return;

        _trayTipShown = true;
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNavigation)
            return;
        if (NavigationList.SelectedItem is NavigationItem item)
            _viewModel.Navigate(item.Key);
    }

    private void OnAddHotkeyRequested(object? sender, DeviceItem? device)
    {
        var choices = BuildDeviceChoices(device?.Id);
        var dialog = new HotkeyEditorWindow(null, _viewModel.Hotkeys.ToList(), choices) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
            _viewModel.AddOrUpdateHotkey(dialog.Result, isNew: true);
    }

    private void OnEditHotkeyRequested(object? sender, HotkeyBinding binding)
    {
        var choices = BuildDeviceChoices(binding.TargetDeviceId);
        var dialog = new HotkeyEditorWindow(binding, _viewModel.Hotkeys.ToList(), choices) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
            _viewModel.AddOrUpdateHotkey(dialog.Result, isNew: false);
    }

    private void OnRenameDeviceRequested(object? sender, DeviceItem? device)
    {
        if (device is null)
            return;

        var dialog = new TextPromptWindow("重命名设备",
            "这个名称只保存在本机，用来在多设备场景中快速识别。", device.Alias) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.ApplyDeviceAlias(device, dialog.Result);
    }

    private List<HotkeyEditorWindow.DeviceChoice> BuildDeviceChoices(string? selectedTargetId)
    {
        var choices = new List<HotkeyEditorWindow.DeviceChoice>
        {
            new(string.Empty, "任意键盘 / 鼠标", true)
        };

        foreach (var device in _viewModel.Devices.OrderBy(d => d.Kind).ThenBy(d => d.DisplayName))
        {
            choices.Add(new HotkeyEditorWindow.DeviceChoice(
                device.Id,
                device.DisplayName + " · " + device.KindLabel,
                false,
                device.Id == selectedTargetId));
        }

        return choices;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => HideToTray(notify: true);

    private void DismissToast_Click(object sender, RoutedEventArgs e)
        => _viewModel.DismissToast();
}
