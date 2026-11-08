using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MouseGuard.Models;

namespace MouseGuard.Dialogs;

public partial class HotkeyEditorWindow : Window
{
    private readonly HotkeyBinding? _existing;
    private readonly IReadOnlyList<HotkeyBinding> _allBindings;
    private readonly List<DeviceChoice> _deviceChoices;
    private HotkeyModifiers _capturedModifiers;
    private int _capturedKey;
    private bool _capturing;
    private bool _loaded;

    public HotkeyEditorWindow(HotkeyBinding? existing, IReadOnlyList<HotkeyBinding> allBindings, IReadOnlyList<DeviceChoice> devices)
    {
        InitializeComponent();
        _existing = existing;
        _allBindings = allBindings;
        _deviceChoices = devices.ToList();

        var actions = Enum.GetValues<HotkeyAction>()
            .Select(a => new ActionChoice(a, HotkeyCatalog.GetActionName(a), HotkeyCatalog.GetActionDescription(a)))
            .ToList();

        ActionSelector.ItemsSource = actions;
        TargetSelector.ItemsSource = _deviceChoices.Where(d => !d.IsAnyDevice).ToList();
        SourceSelector.ItemsSource = _deviceChoices;

        var initialAction = existing?.Action ?? (devices.Any(d => !d.IsAnyDevice)
            ? HotkeyAction.Takeover
            : HotkeyAction.ToggleAllMice);
        ActionSelector.SelectedItem = actions.First(a => a.Action == initialAction);
        TargetSelector.SelectedItem = _deviceChoices.FirstOrDefault(d => d.Id == existing?.TargetDeviceId)
                                     ?? _deviceChoices.FirstOrDefault(d => !d.IsAnyDevice);
        SourceSelector.SelectedItem = _deviceChoices.FirstOrDefault(d => d.Id == existing?.SourceDeviceId)
                                      ?? _deviceChoices[0];

        Title = existing is null ? "新建快捷键" : "编辑快捷键";
        HeaderTitle.Text = Title;
        NameBox.Text = existing?.Name ?? string.Empty;
        _capturedModifiers = existing?.Modifiers ?? HotkeyModifiers.None;
        _capturedKey = existing?.Key ?? 0;

        if (existing?.IsBuiltIn == true)
        {
            NameBox.IsEnabled = false;
            ActionSelector.IsEnabled = false;
            TargetSelector.IsEnabled = false;
            SourceSelector.IsEnabled = false;
            CaptureBox.IsEnabled = false;
            CaptureText.Text = existing.GestureText;
        }
        else
        {
            UpdateCaptureText();
        }

        _loaded = true;
        ActionSelector_SelectionChanged(null, null);
    }

    public HotkeyBinding? Result { get; private set; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ActionSelector_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (!_loaded)
            return;

        if (ActionSelector.SelectedItem is ActionChoice choice)
        {
            ActionDescription.Text = choice.Description;
            TargetPanel.Visibility = HotkeyCatalog.RequiresTarget(choice.Action) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CaptureBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CaptureBox.Focus();
        BeginCapture();
        e.Handled = true;
    }

    private void CaptureBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        BeginCapture();
    }

    private void CaptureBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("StrokeStrongBrush");
        EndCapture();
    }

    private void BeginCapture()
    {
        if (_existing?.IsBuiltIn == true)
            return;
        _capturing = true;
        CaptureState.Text = "录制中";
        CaptureText.Text = _capturedKey == 0 ? "请按下组合键…" : HotkeyFormatter.Format(_capturedModifiers, _capturedKey);
        CaptureHint.Text = "按 Esc 取消录制";
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void EndCapture()
    {
        _capturing = false;
        CaptureState.Text = "录制";
        CaptureHint.Text = "支持 Ctrl / Alt / Shift / Win 与字母、数字、功能键或鼠标侧键";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!_capturing)
        {
            if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndCapture();
            e.Handled = true;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None)
        {
            CaptureText.Text = BuildModifierPreview();
            e.Handled = true;
            return;
        }

        if (key == Key.Tab || key == Key.Enter)
            return;

        var modifiers = ToModifiers(Keyboard.Modifiers);
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return;

        _capturedModifiers = modifiers;
        _capturedKey = virtualKey;
        CaptureText.Text = HotkeyFormatter.Format(_capturedModifiers, _capturedKey);
        ValidationText.Visibility = Visibility.Collapsed;
        EndCapture();
        e.Handled = true;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (!_capturing)
            return;

        var key = e.ChangedButton switch
        {
            MouseButton.Middle => VirtualKeys.MouseMiddle,
            MouseButton.XButton1 => VirtualKeys.MouseX1,
            MouseButton.XButton2 => VirtualKeys.MouseX2,
            _ => 0
        };

        if (key == 0)
            return;

        _capturedModifiers = ToModifiers(Keyboard.Modifiers);
        _capturedKey = key;
        CaptureText.Text = HotkeyFormatter.Format(_capturedModifiers, _capturedKey);
        EndCapture();
        e.Handled = true;
    }

    private string BuildModifierPreview()
    {
        var modifiers = ToModifiers(Keyboard.Modifiers);
        var text = HotkeyFormatter.Format(modifiers, 0);
        return text == "未设置" ? "请按下组合键…" : text.Replace(" + 未设置", "") + " + …";
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }

    private void UpdateCaptureText()
    {
        CaptureText.Text = _capturedKey == 0 ? "点击这里，然后按下组合键" : HotkeyFormatter.Format(_capturedModifiers, _capturedKey);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Visibility = Visibility.Collapsed;

        if (_capturedKey == 0)
        {
            ShowValidation("请先录制一个快捷键组合。");
            return;
        }

        if (VirtualKeys.IsMouse(_capturedKey) && _capturedModifiers == HotkeyModifiers.None)
        {
            ShowValidation("鼠标侧键需要搭配 Ctrl、Alt 或 Shift 使用，避免误触。");
            return;
        }

        if (!VirtualKeys.IsMouse(_capturedKey) && _capturedModifiers == HotkeyModifiers.None &&
            _capturedKey is < 0x70 or > 0x87)
        {
            ShowValidation("单独按键容易与正常输入冲突，请至少添加一个修饰键，或使用 F1–F24。");
            return;
        }

        if (ActionSelector.SelectedItem is not ActionChoice action)
            return;

        DeviceChoice? target = null;
        if (HotkeyCatalog.RequiresTarget(action.Action))
        {
            target = TargetSelector.SelectedItem as DeviceChoice;
            if (target is null || target.IsAnyDevice)
            {
                ShowValidation("请选择快捷键要控制的目标设备。");
                return;
            }
        }

        var source = SourceSelector.SelectedItem as DeviceChoice;
        var conflict = _allBindings.FirstOrDefault(b =>
            b.Id != _existing?.Id && b.IsEnabled &&
            Normalize(b.Modifiers) == Normalize(_capturedModifiers) && b.Key == _capturedKey);
        if (conflict is not null)
        {
            ShowValidation($"这个组合已经绑定给「{conflict.ActionName}」，请换一个组合。");
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = action.Name;

        Result = new HotkeyBinding
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Action = action.Action,
            Modifiers = _capturedModifiers,
            Key = _capturedKey,
            TargetDeviceId = target?.Id ?? string.Empty,
            TargetDeviceName = target?.DisplayName ?? string.Empty,
            SourceDeviceId = source is null || source.IsAnyDevice ? string.Empty : source.Id,
            SourceDeviceName = source is null || source.IsAnyDevice ? string.Empty : source.DisplayName,
            IsEnabled = _existing?.IsEnabled ?? true,
            IsBuiltIn = _existing?.IsBuiltIn ?? false
        };

        DialogResult = true;
        Close();
    }

    private static HotkeyModifiers Normalize(HotkeyModifiers modifiers)
        => modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win);

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public sealed class ActionChoice
    {
        public ActionChoice(HotkeyAction action, string name, string description)
        {
            Action = action;
            Name = name;
            Description = description;
        }

        public HotkeyAction Action { get; }
        public string Name { get; }
        public string Description { get; }

        public override string ToString() => Name;
    }

    public sealed class DeviceChoice
    {
        public DeviceChoice(string id, string displayName, bool isAnyDevice, bool isSelectedByDefault = false)
        {
            Id = id;
            DisplayName = displayName;
            IsAnyDevice = isAnyDevice;
            IsSelectedByDefault = isSelectedByDefault;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool IsAnyDevice { get; }
        public bool IsSelectedByDefault { get; }

        public override string ToString() => DisplayName;
    }
}
