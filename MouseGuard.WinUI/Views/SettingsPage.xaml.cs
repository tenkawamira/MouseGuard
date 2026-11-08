using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MouseGuard.Models;
using MouseGuard.WinUI.ViewModels;

namespace MouseGuard.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loadingTheme;

    public SettingsPage(WinMainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        _loadingTheme = true;
        ThemeCombo.SelectedIndex = viewModel.Settings.Theme switch
        {
            AppTheme.Dark => 1,
            AppTheme.Light => 2,
            _ => 0
        };
        _loadingTheme = false;
    }

    public WinMainViewModel ViewModel { get; }

    private void Elevate_Click(object sender, RoutedEventArgs e) => ViewModel.Elevate();

    private void OpenData_Click(object sender, RoutedEventArgs e) => ViewModel.OpenDataFolder();

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;

        if (Enum.TryParse<AppTheme>(tag, true, out var theme))
            ViewModel.SetTheme(theme);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复默认设置",
            Content = "当前快捷键、设备别名和提示样式将被清除，已接管的设备会被释放。",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.ResetAll();
    }
}
