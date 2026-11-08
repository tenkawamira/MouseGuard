namespace MouseGuard.Models;

public enum DeviceKind
{
    Mouse,
    Keyboard
}

public enum DeviceState
{
    Active,
    Disabled,
    Unavailable
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public enum HotkeyAction
{
    ToggleTakeover,
    Takeover,
    Release,
    ToggleAllMice,
    TakeoverAllMice,
    ReleaseAll,
    ShowTakeoverNotice
}

public enum PopupPosition
{
    Center,
    TopRight,
    BottomRight
}

public enum AppTheme
{
    Dark,
    Light,
    System
}

public enum StatusTone
{
    Success,
    Warning,
    Muted,
    Accent
}
