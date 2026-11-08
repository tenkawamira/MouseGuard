using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MouseGuard.Models;
using MouseGuard.WinUI.ViewModels;

namespace MouseGuard.WinUI.Views;

public sealed partial class HotkeysPage : Page
{
    public HotkeysPage(WinMainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public WinMainViewModel ViewModel { get; }

    private async void New_Click(object sender, RoutedEventArgs e)
        => await HotkeyEditor.ShowAsync(XamlRoot, ViewModel);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HotkeyBinding binding })
            await HotkeyEditor.ShowAsync(XamlRoot, ViewModel, existing: binding);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HotkeyBinding binding })
            ViewModel.DeleteHotkey(binding);
    }

    private void HotkeyToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: HotkeyBinding binding })
            ViewModel.ToggleHotkey(binding);
    }
}
