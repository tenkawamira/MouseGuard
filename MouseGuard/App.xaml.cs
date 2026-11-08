using System.Windows;
using System.Windows.Threading;
using MouseGuard.Native;
using MouseGuard.Services;
using MouseGuard.ViewModels;

namespace MouseGuard;

public partial class App : System.Windows.Application
{
    private SettingsService? _settingsService;
    private SingleInstanceService? _singleInstance;
    private TrayService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private RawInputService? _rawInput;
    private InputHookService? _hooks;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var earlyDataDir = Environment.GetEnvironmentVariable("MOUSEGUARD_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(earlyDataDir))
        {
            try
            {
                LogService.Initialize(Path.Combine(earlyDataDir, "logs"));
                LogService.Info("OnStartup begin");
            }
            catch
            {
            }
        }

        var replace = e.Args.Any(a => string.Equals(a, "--replace", StringComparison.OrdinalIgnoreCase));
        var startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        var pageArgIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        var startPage = pageArgIndex >= 0 && pageArgIndex + 1 < e.Args.Length ? e.Args[pageArgIndex + 1] : null;
        var previewPopup = e.Args.Any(a => string.Equals(a, "--preview-popup", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            if (replace && _singleInstance.WaitForExistingToExit(TimeSpan.FromSeconds(8)))
            {
                // We now own the mutex; continue startup.
            }
            else
            {
                _singleInstance.TryActivateExisting();
                Shutdown();
                return;
            }
        }

        _settingsService = new SettingsService();
        _settingsService.Load();
        LogService.Initialize(_settingsService.LogDirectory);
        LogService.Info("MouseGuard 启动");

        var theme = new ThemeService();
        theme.Apply(_settingsService.Settings.Theme);

        var devices = new DeviceService();
        var autoStart = new AutoStartService();
        _rawInput = new RawInputService(raw => devices.ResolveRawPath(raw));
        _hooks = new InputHookService(ResolveSourceDevice);

        LogService.Info("创建 ViewModel");
        _viewModel = new MainViewModel(_settingsService, devices, autoStart, theme, _rawInput, _hooks);
        LogService.Info("创建主窗口");
        _mainWindow = new MainWindow(_viewModel);
        MainWindow = _mainWindow;
        LogService.Info("主窗口创建完成");

        _tray = new TrayService(_settingsService.Settings.LaunchAtStartup, miceLocked: false);
        _tray.OpenRequested += (_, _) => _mainWindow.ShowFromTray();
        _tray.ToggleMiceRequested += (_, _) => _viewModel.ToggleMiceCommand.Execute(null);
        _tray.ReleaseAllRequested += (_, _) => _viewModel.ReleaseAllCommand.Execute(null);
        _tray.ShowPopupRequested += (_, _) => _viewModel.ShowPopupCommand.Execute(null);
        _tray.ExitRequested += (_, _) => ExitApplication();

        _viewModel.LockStateChanged += (_, _) =>
        {
            _tray.SetMiceLocked(_viewModel.MiceLocked);
        };

        _singleInstance.StartActivationServer();
        _singleInstance.ActivationRequested += (_, _) => _mainWindow.ShowFromTray();
        _mainWindow.HiddenToTray += (_, _) =>
            _tray?.ShowBalloon("鼠标卫士仍在后台运行", "快捷键继续生效；双击托盘图标可重新打开控制台。");

        SessionEnding += (_, _) => _viewModel.Shutdown();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Error("未处理异常", args.ExceptionObject as Exception);

        var shouldStartMinimized = startInTray || (_settingsService.Settings.StartMinimized && !replace);
        LogService.Info($"显示模式: {(shouldStartMinimized ? "tray" : "window")}");
        if (shouldStartMinimized)
        {
            _mainWindow.PrepareHidden();
        }
        else
        {
            _mainWindow.Show();
        }
        LogService.Info("主窗口显示完成");

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _viewModel.InitializeAsync();
                LogService.Info($"启动页面参数: {startPage ?? "(none)"}");
                if (!string.IsNullOrWhiteSpace(startPage))
                    _viewModel.Navigate(startPage);
                if (previewPopup)
                    _viewModel.ShowTakeoverPopup("已连接的鼠标", Models.DeviceKind.Mouse, force: true);
                _tray?.SetStartup(_settingsService.Settings.LaunchAtStartup);
                if (shouldStartMinimized)
                {
                    _tray?.ShowBalloon("鼠标卫士正在后台运行", "双击托盘图标打开控制台。按 Ctrl+Alt+Shift+M 可快速接管鼠标。");
                }
            }
            catch (Exception ex)
            {
                LogService.Error("初始化失败", ex);
                _viewModel.ShowToast("初始化遇到问题，请查看日志。", Models.StatusTone.Warning);
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private string? ResolveSourceDevice(string? key)
    {
        if (_rawInput is null)
            return null;

        return key switch
        {
            "__LAST_MOUSE__" => _rawInput.LastMouseDeviceId,
            "__LAST_KEYBOARD__" => _rawInput.LastKeyboardDeviceId,
            _ => null
        };
    }

    public void ExitApplication()
    {
        if (_exiting)
            return;
        _exiting = true;
        _viewModel?.Shutdown();
        _mainWindow?.ForceClose();
        Cleanup();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Shutdown();
        Cleanup();
        LogService.Info("MouseGuard 退出");
        base.OnExit(e);
    }

    private void Cleanup()
    {
        try
        {
            _tray?.Dispose();
            _tray = null;
            _rawInput?.Dispose();
            _rawInput = null;
            _hooks?.Dispose();
            _hooks = null;
            _settingsService?.Save();
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
        catch (Exception ex)
        {
            LogService.Error("清理资源失败", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("UI 线程异常", e.Exception);
        try
        {
            _viewModel?.ShowToast("发生了一个错误，详细信息已写入日志。", Models.StatusTone.Warning);
        }
        catch
        {
        }

        e.Handled = true;
    }
}
