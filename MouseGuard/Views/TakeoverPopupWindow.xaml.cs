using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseGuard.Models;
using MouseGuard.Native;
using DrawingSize = System.Drawing.Size;
using MediaColor = System.Windows.Media.Color;

namespace MouseGuard.Views;

public partial class TakeoverPopupWindow : Window
{
    private readonly PopupSettings _settings;
    private readonly DispatcherTimer _closeTimer;
    private bool _closing;

    public TakeoverPopupWindow(PopupSettings settings, string title, string? body, string? deviceName, DeviceKind kind)
    {
        InitializeComponent();
        _settings = settings.Clone();

        TitleText.Text = string.IsNullOrWhiteSpace(_settings.Title) ? title : _settings.Title;
        MessageText.Text = string.IsNullOrWhiteSpace(body) ? _settings.Message : body;
        DeviceGlyph.Text = kind == DeviceKind.Mouse ? "\uE962" : "\uE765";

        if (_settings.ShowFooterBranding && !string.IsNullOrWhiteSpace(_settings.FooterText))
        {
            FooterBrandText.Text = _settings.FooterText.Trim();
            FooterBrandText.Visibility = Visibility.Visible;
        }

        if (_settings.ShowDeviceName && !string.IsNullOrWhiteSpace(deviceName))
        {
            DeviceNameText.Text = deviceName;
        }
        else
        {
            DeviceChip.Visibility = Visibility.Collapsed;
        }

        ApplyAccent(_settings.Accent);

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.DurationSeconds, 2, 30))
        };
        _closeTimer.Tick += (_, _) => CloseWithAnimation();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += (_, _) => CloseWithAnimation();
    }

    private void ApplyAccent(string? accent)
    {
        var color = accent switch
        {
            "Violet" => MediaColor.FromRgb(0xA7, 0x8B, 0xFA),
            "Green" => MediaColor.FromRgb(0x5B, 0xD6, 0xA6),
            _ => MediaColor.FromRgb(0x6D, 0x8D, 0xFF)
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        LockBadge.Background = brush;
        DeviceDot.Fill = brush;
        CountdownBar.Background = brush;
        KickerText.Foreground = brush;
    }

    private void CaptureAcrylicBackdrop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
                return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 24 || height < 24)
                return;

            using var screenshot = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(screenshot))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new DrawingSize(width, height),
                    CopyPixelOperation.SourceCopy);
            }

            using var blurred = BoxBlur(screenshot, 32);
            var hBitmap = blurred.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();

                var brush = new ImageBrush(source)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                Card.Background = brush;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            // Keep the translucent gradient fallback when screen capture is unavailable.
        }
    }

    private static Bitmap BoxBlur(Bitmap source, int downscale)
    {
        var smallWidth = Math.Max(1, source.Width / downscale);
        var smallHeight = Math.Max(1, source.Height / downscale);

        using var small = new Bitmap(smallWidth, smallHeight);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, smallWidth, smallHeight);
        }

        var result = new Bitmap(source.Width, source.Height);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(small, 0, 0, result.Width, result.Height);
        }

        return result;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetNoActivate(hwnd);
        NativeMethods.ApplyWindowStyle(hwnd, dark: true, roundCorners: false);
        NativeMethods.RemoveWindowBorder(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        CaptureAcrylicBackdrop();

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        var hwndForDwm = hwnd;
        NativeMethods.ApplyWindowStyle(hwndForDwm, dark: true, roundCorners: false);
        NativeMethods.RemoveWindowBorder(hwndForDwm);

        Card.RenderTransform = new TranslateTransform(0, 16);
        var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);

        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);

        var duration = TimeSpan.FromSeconds(Math.Clamp(_settings.DurationSeconds, 2, 30));
        var shrink = new DoubleAnimation(1, 0, duration);
        CountdownScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, shrink);

        _closeTimer.Start();
    }

    private void PositionWindow()
    {
        var work = SystemParameters.WorkArea;
        var screen = work;
        try
        {
            if (NativeMethods.GetCursorPos(out var point))
            {
                var monitor = NativeMethods.MonitorFromPoint(point, 2);
                var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    screen = new Rect(info.rcWork.Left, info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
                }
            }
        }
        catch
        {
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 304;

        switch (_settings.Position)
        {
            case PopupPosition.TopRight:
                Left = screen.Right - width - 24;
                Top = screen.Top + 24;
                break;
            case PopupPosition.BottomRight:
                Left = screen.Right - width - 24;
                Top = screen.Bottom - height - 24;
                break;
            default:
                Left = screen.Left + (screen.Width - width) / 2;
                Top = screen.Top + (screen.Height - height) / 2;
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void CloseWithAnimation()
    {
        if (_closing)
            return;
        _closing = true;
        _closeTimer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
