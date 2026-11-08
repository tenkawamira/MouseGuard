using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MouseGuard.Helpers;

namespace MouseGuard.Models;

public static class VirtualKeys
{
    public const int MouseLeft = 0x201;
    public const int MouseRight = 0x202;
    public const int MouseMiddle = 0x204;
    public const int MouseX1 = 0x205;
    public const int MouseX2 = 0x206;

    public static bool IsMouse(int key) => key is >= MouseLeft and <= MouseX2;
}

public static class HotkeyCatalog
{
    /// <summary>
    /// Global "all mice" actions are intentionally not offered anymore.
    /// A shortcut always targets one or more explicitly selected devices.
    /// </summary>
    public static readonly HotkeyAction[] SelectableActions =
    {
        HotkeyAction.ToggleTakeover,
        HotkeyAction.Takeover,
        HotkeyAction.Release,
        HotkeyAction.ReleaseAll,
        HotkeyAction.ShowTakeoverNotice
    };

    public static string GetActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTakeover => "切换所选设备",
        HotkeyAction.Takeover => "接管所选设备",
        HotkeyAction.Release => "释放所选设备",
        HotkeyAction.ToggleAllMice => "切换全局鼠标接管（已停用）",
        HotkeyAction.TakeoverAllMice => "接管全部鼠标（已停用）",
        HotkeyAction.ReleaseAll => "紧急释放全部控制",
        HotkeyAction.ShowTakeoverNotice => "弹出接管提示",
        _ => action.ToString()
    };

    public static string GetActionDescription(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTakeover => "按一次接管所选设备，再按一次全部释放",
        HotkeyAction.Takeover => "接管所选的一台或多台设备，直到手动释放",
        HotkeyAction.Release => "恢复所选设备的控制权",
        HotkeyAction.ToggleAllMice => "该动作已停用，请改用设备级快捷键",
        HotkeyAction.TakeoverAllMice => "该动作已停用，请改用设备级快捷键",
        HotkeyAction.ReleaseAll => "立即释放全部已接管设备；不接管任何设备",
        HotkeyAction.ShowTakeoverNotice => "仅显示「鼠标已被接管」提示，不改变控制状态",
        _ => string.Empty
    };

    public static bool RequiresTarget(HotkeyAction action)
        => action is HotkeyAction.ToggleTakeover or HotkeyAction.Takeover or HotkeyAction.Release;
}

public sealed class HotkeyBinding : ObservableObject
{
    private string _name = string.Empty;
    private HotkeyAction _action;
    private HotkeyModifiers _modifiers;
    private int _key;
    private string _targetDeviceId = string.Empty;
    private string _targetDeviceName = string.Empty;
    private List<string> _targetDeviceIds = new();
    private List<string> _targetDeviceNames = new();
    private string _sourceDeviceId = string.Empty;
    private string _sourceDeviceName = string.Empty;
    private bool _isEnabled = true;
    private bool _isBuiltIn;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public HotkeyAction Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                OnPropertyChanged(nameof(ActionName));
                OnPropertyChanged(nameof(ActionDescription));
            }
        }
    }

    public HotkeyModifiers Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetProperty(ref _modifiers, value))
                OnPropertyChanged(nameof(GestureText));
        }
    }

    public int Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
                OnPropertyChanged(nameof(GestureText));
        }
    }

    public string TargetDeviceId
    {
        get => _targetDeviceId;
        set => SetProperty(ref _targetDeviceId, value);
    }

    public string TargetDeviceName
    {
        get => _targetDeviceName;
        set
        {
            if (SetProperty(ref _targetDeviceName, value))
                OnPropertyChanged(nameof(TargetSummary));
        }
    }

    /// <summary>
    /// One shortcut can control multiple devices. The two legacy singular
    /// properties above are kept so settings written by 1.x still load.
    /// </summary>
    public List<string> TargetDeviceIds
    {
        get => _targetDeviceIds;
        set
        {
            _targetDeviceIds = value ?? new List<string>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetSummary));
        }
    }

    public List<string> TargetDeviceNames
    {
        get => _targetDeviceNames;
        set
        {
            _targetDeviceNames = value ?? new List<string>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetSummary));
        }
    }

    public void EnsureTargets()
    {
        TargetDeviceIds ??= new List<string>();
        TargetDeviceNames ??= new List<string>();
        if (TargetDeviceIds.Count == 0 && !string.IsNullOrWhiteSpace(TargetDeviceId))
            TargetDeviceIds.Add(TargetDeviceId);
        if (TargetDeviceNames.Count == 0 && !string.IsNullOrWhiteSpace(TargetDeviceName))
            TargetDeviceNames.Add(TargetDeviceName);

        if (!string.IsNullOrWhiteSpace(TargetDeviceId) &&
            !TargetDeviceIds.Contains(TargetDeviceId, StringComparer.OrdinalIgnoreCase))
            TargetDeviceIds.Add(TargetDeviceId);
        if (!string.IsNullOrWhiteSpace(TargetDeviceName) &&
            !TargetDeviceNames.Contains(TargetDeviceName, StringComparer.OrdinalIgnoreCase))
            TargetDeviceNames.Add(TargetDeviceName);
    }

    public string SourceDeviceId
    {
        get => _sourceDeviceId;
        set => SetProperty(ref _sourceDeviceId, value);
    }

    public string SourceDeviceName
    {
        get => _sourceDeviceName;
        set
        {
            if (SetProperty(ref _sourceDeviceName, value))
                OnPropertyChanged(nameof(SourceSummary));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsBuiltIn
    {
        get => _isBuiltIn;
        set => SetProperty(ref _isBuiltIn, value);
    }

    [JsonIgnore]
    public string GestureText => HotkeyFormatter.Format(Modifiers, Key);

    [JsonIgnore]
    public string ActionName => HotkeyCatalog.GetActionName(Action);

    [JsonIgnore]
    public string ActionDescription => HotkeyCatalog.GetActionDescription(Action);

    [JsonIgnore]
    public string TargetSummary
    {
        get
        {
            if (!HotkeyCatalog.RequiresTarget(Action))
                return "不需要目标设备";
            var names = (TargetDeviceNames ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (names.Count == 0 && !string.IsNullOrWhiteSpace(TargetDeviceName))
                names.Add(TargetDeviceName);
            if (names.Count == 0)
                return "尚未选择目标设备";
            if (names.Count <= 2)
                return string.Join("、", names);
            return $"{names[0]} 等 {names.Count} 台设备";
        }
    }

    [JsonIgnore]
    public string SourceSummary => string.IsNullOrWhiteSpace(SourceDeviceName)
        ? "任意键盘 / 鼠标"
        : SourceDeviceName;

    public HotkeyBinding Clone() => new()
    {
        Id = Id,
        Name = Name,
        Action = Action,
        Modifiers = Modifiers,
        Key = Key,
        TargetDeviceId = TargetDeviceId,
        TargetDeviceName = TargetDeviceName,
        TargetDeviceIds = new List<string>(TargetDeviceIds ?? new List<string>()),
        TargetDeviceNames = new List<string>(TargetDeviceNames ?? new List<string>()),
        SourceDeviceId = SourceDeviceId,
        SourceDeviceName = SourceDeviceName,
        IsEnabled = IsEnabled,
        IsBuiltIn = IsBuiltIn
    };
}

public static class HotkeyFormatter
{
    public static string Format(HotkeyModifiers modifiers, int key)
    {
        if (key == 0)
            return "未设置";

        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");

        parts.Add(GetKeyName(key));
        return string.Join(" + ", parts);
    }

    public static string GetKeyName(int key) => key switch
    {
        VirtualKeys.MouseLeft => "鼠标左键",
        VirtualKeys.MouseRight => "鼠标右键",
        VirtualKeys.MouseMiddle => "鼠标中键",
        VirtualKeys.MouseX1 => "鼠标侧键 1",
        VirtualKeys.MouseX2 => "鼠标侧键 2",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5B => "左 Win",
        0x5C => "右 Win",
        0x5D => "菜单键",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        >= 0x60 and <= 0x69 => "Num " + (key - 0x60),
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        _ => $"VK {key:X2}"
    };
}
