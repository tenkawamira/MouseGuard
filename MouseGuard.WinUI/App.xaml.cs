using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;
using MouseGuard.Services;

namespace MouseGuard.WinUI;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private Thread? _activationThread;
    private volatile bool _shuttingDown;
    private SettingsService? _settings;
    private DeviceService? _devices;
    private AutoStartService? _autoStart;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogService.Error("WinUI 未处理异常", e.Exception);
            e.Handled = true;
        };
    }

    public static new App Current => (App)Application.Current;
    public MainWindow? MainWindow => _window;

    public SettingsService Settings => _settings!;
    public DeviceService Devices => _devices!;
    public AutoStartService AutoStart => _autoStart!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var startInTray = Array.Exists(commandLine, a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        var pageIndex = Array.FindIndex(commandLine, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        var startPage = pageIndex >= 0 && pageIndex + 1 < commandLine.Length ? commandLine[pageIndex + 1] : null;
        var previewPopup = Array.Exists(commandLine, a => string.Equals(a, "--popup", StringComparison.OrdinalIgnoreCase));

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\MouseGuard.2.0.0.3.Activate");
        _instanceMutex = new Mutex(true, @"Local\MouseGuard.2.0.0.3.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            try { _activationEvent.Set(); } catch { }
            Exit();
            return;
        }

        _settings = new SettingsService();
        _settings.Load();
        startInTray = startInTray || _settings.Settings.StartMinimized;
        LogService.Initialize(_settings.LogDirectory);
        LogService.Info("MouseGuard WinUI 启动");

        _devices = new DeviceService();
        _autoStart = new AutoStartService();

        try
        {
            _window = new MainWindow(_settings, _devices, _autoStart, startInTray, startPage, previewPopup);
        }
        catch (Exception ex)
        {
            LogService.Error("创建主窗口失败", ex);
            throw;
        }

        _window.Closed += (_, _) => ShutdownServices();

        _activationThread = new Thread(ActivationLoop)
        {
            IsBackground = true,
            Name = "MouseGuard activation"
        };
        _activationThread.Start();

        if (!startInTray)
            _window.Activate();

        _window.InitializeAsync();
    }

    /// <summary>
    /// A second launch is the most reliable way for a user to recover an app
    /// that is already resident in the tray. Signal the existing instance to
    /// show its window instead of silently exiting as 1.x did.
    /// </summary>
    private void ActivationLoop()
    {
        while (!_shuttingDown)
        {
            try
            {
                if (_activationEvent is null || !_activationEvent.WaitOne())
                    break;

                if (_shuttingDown)
                    break;

                _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowFromTray());
            }
            catch
            {
                break;
            }
        }
    }

    private void ShutdownServices()
    {
        _shuttingDown = true;
        try { _activationEvent?.Set(); } catch { }
        _window?.Dispose();
        _settings?.Save();
        _activationEvent?.Dispose();
        _activationEvent = null;
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        LogService.Info("MouseGuard 2.0 退出");
    }
}
