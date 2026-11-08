using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MouseGuard.Models;
using Windows.UI;
using WinRT.Interop;

namespace MouseGuard.WinUI.Views;

public sealed partial class TakeoverWindow : Window
{
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _closeTimer;
    private readonly long _startedAt;
    private readonly double _durationSeconds;
    private bool _closing;

    public TakeoverWindow(PopupSettings settings, DeviceKind kind, string? deviceName)
    {
        InitializeComponent();
        var title = kind == DeviceKind.Mouse ? "鼠标已被接管" : "键盘已被接管";
        TitleText.Text = string.IsNullOrWhiteSpace(settings.Title) ? title : settings.Title;
        MessageText.Text = string.IsNullOrWhiteSpace(settings.Message)
            ? "此设备的控制权已被接管"
            : settings.Message;
        DeviceIcon.Glyph = kind == DeviceKind.Mouse ? "\uE962" : "\uE765";
        DeviceNameText.Text = string.IsNullOrWhiteSpace(deviceName) ? "已连接的设备" : deviceName;
        DeviceChip.Visibility = settings.ShowDeviceName ? Visibility.Visible : Visibility.Collapsed;

        if (settings.ShowFooterBranding && !string.IsNullOrWhiteSpace(settings.FooterText))
        {
            FooterBrandText.Text = settings.FooterText.Trim();
            FooterBrandText.Visibility = Visibility.Visible;
        }

        ApplyAccent(settings.Accent);
        ConfigureWindow(settings.Position);
        ApplyBackdrop();

        Root.PointerPressed += (_, _) => BeginClose();
        Root.Loaded += (_, _) => ApplyRoundedRegion();

        _durationSeconds = Math.Clamp(settings.DurationSeconds, 2, 30);
        _startedAt = Stopwatch.GetTimestamp();

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(33);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.Interval = TimeSpan.FromMilliseconds(110);
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => CompleteClose();
    }

    public void ShowNoActivate()
    {
        try
        {
            AppWindow.Show(false);
        }
        catch
        {
            var handle = WindowNative.GetWindowHandle(this);
            ShowWindow(handle, 4);
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        ApplyRoundedRegion();
    }

    private void UpdateProgress()
    {
        if (_closing)
            return;

        var elapsed = (Stopwatch.GetTimestamp() - _startedAt) / (double)Stopwatch.Frequency;
        var remaining = 1.0 - elapsed / _durationSeconds;
        if (remaining <= 0)
        {
            CountdownScale.ScaleX = 0;
            BeginClose();
            return;
        }

        CountdownScale.ScaleX = remaining;
    }

    private void BeginClose()
    {
        if (_closing)
            return;

        _closing = true;
        _progressTimer.Stop();

        // Hide before destroying the HWND. Closing a visible WinUI window
        // briefly exposes the compositor surface as a white rectangle.
        Root.Opacity = 0;
        try { AppWindow.Hide(); } catch { }
        _closeTimer.Start();
    }

    private void CompleteClose()
    {
        _closeTimer.Stop();
        try { Close(); } catch { }
    }

    private void ApplyRoundedRegion()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            var round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            var border = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));

            GetWindowRect(hwnd, out var rect);
            var radius = (int)(22 * GetDpiForWindow(hwnd) / 96.0);
            var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1,
                radius * 2, radius * 2);
            if (region != IntPtr.Zero)
                SetWindowRgn(hwnd, region, true);
        }
        catch
        {
        }
    }

    private void ConfigureWindow(PopupPosition position)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)(440 * scale);
        var height = (int)(228 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var margin = (int)(24 * scale);

        var x = work.X + (work.Width - width) / 2;
        var y = work.Y + (work.Height - height) / 2;
        switch (position)
        {
            case PopupPosition.TopRight:
                x = work.X + work.Width - width - margin;
                y = work.Y + margin;
                break;
            case PopupPosition.BottomRight:
                x = work.X + work.Width - width - margin;
                y = work.Y + work.Height - height - margin;
                break;
        }

        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // Remove any remaining non-client frame; the card paints its own
        // opaque surface and the window region supplies the rounded silhouette.
        var style = GetWindowLong(hwnd, -16);
        style &= ~(0x00C00000 | 0x00800000 | 0x00400000 | 0x00040000);
        SetWindowLong(hwnd, -16, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0004 | 0x0020);

        try
        {
            var round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            var border = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch
        {
        }
    }

    private void ApplyBackdrop()
    {
        var theme = AppTheme.Dark;
        try { theme = App.Current.Settings.Settings.Theme; } catch { }

        Root.RequestedTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };

        // An opaque card is intentional here. Borderless WinUI windows do
        // not reliably receive Mica/acrylic on every Windows configuration;
        // this keeps the prompt legible and consistent everywhere.
        var light = theme == AppTheme.Light ||
                    (theme == AppTheme.System && Root.ActualTheme == ElementTheme.Light);
        Root.Background = light
            ? new SolidColorBrush(Color.FromArgb(255, 244, 244, 244))
            : new SolidColorBrush(Color.FromArgb(255, 32, 32, 32));
    }

    private void ApplyAccent(string? accent)
    {
        var color = accent switch
        {
            "Violet" => Color.FromArgb(255, 0xA7, 0x8B, 0xFA),
            "Green" => Color.FromArgb(255, 0x5B, 0xD6, 0xA6),
            _ => Color.FromArgb(255, 0x60, 0xA5, 0xFA)
        };
        var brush = new SolidColorBrush(color);
        LockBadge.Background = brush;
        DeviceDot.Fill = brush;
        CountdownBar.Background = brush;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);


    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
