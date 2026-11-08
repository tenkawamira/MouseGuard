using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MouseGuard.Models;
using MouseGuard.WinUI.ViewModels;

namespace MouseGuard.WinUI.Views;

public sealed partial class OverviewPage : Page
{
    public OverviewPage(WinMainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public WinMainViewModel ViewModel { get; }

    private async void ReleaseAll_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ReleaseAllAsync("主页操作");

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshDevicesAsync();

    private async void DeviceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: DeviceItem item } toggle)
            return;
        if (toggle.IsOn == item.IsTakenOver)
            return;

        await ViewModel.SetDeviceTakenAsync(item, toggle.IsOn);
        if (toggle.IsOn != item.IsTakenOver)
            toggle.IsOn = item.IsTakenOver;
    }

    private void Devices_Click(object sender, RoutedEventArgs e)
        => App.Current.MainWindow?.Navigate("devices");
}
