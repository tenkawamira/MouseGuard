using System.Windows;
using System.Windows.Controls;
using MouseGuard.Models;

namespace MouseGuard.Controls;

public partial class DeviceGlyph : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(DeviceKind), typeof(DeviceGlyph),
        new PropertyMetadata(DeviceKind.Mouse, OnKindChanged));

    public DeviceGlyph()
    {
        InitializeComponent();
        UpdateKind(Kind);
    }

    public DeviceKind Kind
    {
        get => (DeviceKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DeviceGlyph)d).UpdateKind((DeviceKind)e.NewValue);

    private void UpdateKind(DeviceKind kind)
    {
        var mouse = kind == DeviceKind.Mouse;
        MouseShape.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        WheelMark.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        MouseSplit.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
        KeyboardShape.Visibility = mouse ? Visibility.Collapsed : Visibility.Visible;
        KeyboardKeys.Visibility = mouse ? Visibility.Collapsed : Visibility.Visible;
    }
}
