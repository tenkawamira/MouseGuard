using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MouseGuard.WinUI.ViewModels;

namespace MouseGuard.WinUI.Views;

public sealed partial class NotificationsPage : Page
{
    private bool _syncing;

    public NotificationsPage(WinMainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        _syncing = true;
        DurationSlider.Minimum = 2;
        DurationSlider.Maximum = 15;
        DurationSlider.StepFrequency = 1;
        DurationSlider.Value = viewModel.Settings.Popup.DurationSeconds;
        _syncing = false;
    }

    public WinMainViewModel ViewModel { get; }

    private void Duration_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
            return;

        var value = (int)Math.Round(e.NewValue);
        if (value == ViewModel.Settings.Popup.DurationSeconds)
            return;
        ViewModel.Settings.Popup.DurationSeconds = value;
    }

    private void Test_Click(object sender, RoutedEventArgs e) => ViewModel.ShowTestPopup();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetPopup();
        _syncing = true;
        DurationSlider.Value = ViewModel.Settings.Popup.DurationSeconds;
        _syncing = false;
    }
}
