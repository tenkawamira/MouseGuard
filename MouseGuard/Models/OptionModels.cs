namespace MouseGuard.Models;

public sealed class PopupPositionOption
{
    public PopupPositionOption(PopupPosition value, string label, string description)
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public PopupPosition Value { get; }
    public string Label { get; }
    public string Description { get; }

    public override string ToString() => Label;
}

public sealed class AccentOption
{
    public AccentOption(string value, string label, string color)
    {
        Value = value;
        Label = label;
        Color = color;
    }

    public string Value { get; }
    public string Label { get; }
    public string Color { get; }
}
