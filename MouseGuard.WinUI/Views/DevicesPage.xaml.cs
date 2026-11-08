using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MouseGuard.Models;
using MouseGuard.WinUI.ViewModels;

namespace MouseGuard.WinUI.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage(WinMainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public WinMainViewModel ViewModel { get; }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshDevicesAsync();

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null && FilterCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            ViewModel.SetFilter(tag);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            ViewModel.SearchText = box.Text;
    }

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

    private async void BindHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceItem item })
            await HotkeyEditor.ShowAsync(XamlRoot, ViewModel, item.Id);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeviceItem item })
            return;

        var box = new TextBox { Text = item.Alias, PlaceholderText = item.DisplayName, MaxLength = 40 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名设备",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.ApplyDeviceAlias(item, box.Text);
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => ViewModel.Elevate();
}
