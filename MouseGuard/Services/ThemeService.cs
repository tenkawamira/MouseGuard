using System.Windows;
using Microsoft.Win32;
using MouseGuard.Models;

namespace MouseGuard.Services;

public sealed class ThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Dark;
    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        var effective = theme == AppTheme.System ? (IsSystemLight() ? AppTheme.Light : AppTheme.Dark) : theme;
        var dark = effective == AppTheme.Dark;
        Current = theme;
        IsDark = dark;

        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            d.Source?.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) == true);

        var replacement = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Add(replacement);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }
}
