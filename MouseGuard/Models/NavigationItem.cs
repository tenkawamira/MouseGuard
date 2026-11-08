using MouseGuard.Helpers;

namespace MouseGuard.Models;

public sealed class NavigationItem : ObservableObject
{
    private bool _isSelected;

    public NavigationItem(string key, string label, string glyph, string description)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Description = description;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ActivityEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Text { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public StatusTone Tone { get; init; } = StatusTone.Accent;
    public string Glyph { get; init; } = "\uE73E";
    public string TimeText => Time.ToString("HH:mm");
}
