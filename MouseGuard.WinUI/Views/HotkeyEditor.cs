using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MouseGuard.Models;
using MouseGuard.WinUI.ViewModels;
using Windows.System;

namespace MouseGuard.WinUI.Views;

internal static class HotkeyEditor
{
    public static async Task ShowAsync(XamlRoot xamlRoot, WinMainViewModel vm, string? preselectedTargetId = null,
        HotkeyBinding? existing = null)
    {
        var devices = vm.Devices.ToList();
        var actions = HotkeyCatalog.SelectableActions
            .Select(a => new ActionChoice(a, HotkeyCatalog.GetActionName(a), HotkeyCatalog.GetActionDescription(a)))
            .ToList();

        var choices = new List<DeviceChoice> { new(string.Empty, "任意键盘 / 鼠标", true) };
        choices.AddRange(devices.Select(d => new DeviceChoice(d.Id, d.DisplayName + " · " + d.KindLabel, false)));
        var targetChoices = choices.Where(c => !c.IsAnyDevice).ToList();

        var nameBox = new TextBox { PlaceholderText = "例如：接管学生鼠标" };
        var enabledSwitch = new ToggleSwitch
        {
            Header = "启用此快捷键",
            IsOn = existing?.IsEnabled ?? true
        };
        var actionCombo = new ComboBox
        {
            ItemsSource = actions,
            DisplayMemberPath = nameof(ActionChoice.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var targetList = new ListView
        {
            ItemsSource = targetChoices,
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = true,
            MaxHeight = 152,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var sourceCombo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(DeviceChoice.DisplayName),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var targetPanel = new StackPanel { Spacing = 4 };
        targetPanel.Children.Add(new TextBlock { Text = "目标设备（可多选）", FontSize = 12 });
        targetPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Child = targetList
        });

        var sourcePanel = new StackPanel { Spacing = 4 };
        sourcePanel.Children.Add(new TextBlock { Text = "来源设备（可选）", FontSize = 12 });
        sourcePanel.Children.Add(sourceCombo);

        var captureButton = new Button
        {
            Content = "点击后按下组合键",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 64,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10)
        };
        var captureHint = new TextBlock
        {
            Text = "支持 Ctrl / Alt / Shift / Win 与字母、数字、功能键，或鼠标中键 / 侧键。",
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var errorText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var actionDescription = new TextBlock
        {
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "名称", FontSize = 12 });
        panel.Children.Add(nameBox);
        panel.Children.Add(enabledSwitch);
        panel.Children.Add(new TextBlock { Text = "动作", FontSize = 12 });
        panel.Children.Add(actionCombo);
        panel.Children.Add(actionDescription);
        panel.Children.Add(targetPanel);
        panel.Children.Add(sourcePanel);
        panel.Children.Add(new TextBlock { Text = "触发组合", FontSize = 12 });
        panel.Children.Add(captureButton);
        panel.Children.Add(captureHint);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = existing is null ? "新建快捷键" : "编辑快捷键",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var capturedModifiers = existing?.Modifiers ?? HotkeyModifiers.None;
        var capturedKey = existing?.Key ?? 0;
        var capturing = false;
        var updateTargetVisibility = new Action(() =>
        {
            if (actionCombo.SelectedItem is ActionChoice choice)
                targetPanel.Visibility = HotkeyCatalog.RequiresTarget(choice.Action)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        });

        actionCombo.SelectionChanged += (_, _) =>
        {
            if (actionCombo.SelectedItem is ActionChoice choice)
                actionDescription.Text = choice.Description;
            updateTargetVisibility();
        };

        captureButton.Click += (_, _) =>
        {
            capturing = true;
            captureButton.Content = "请按下组合键…";
            captureButton.Focus(FocusState.Programmatic);
            errorText.Visibility = Visibility.Collapsed;
        };

        captureButton.KeyDown += (_, e) =>
        {
            if (!capturing)
                return;

            var key = (int)e.Key;
            if (key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            {
                captureButton.Content = BuildPreview(capturedKey);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                capturing = false;
                captureButton.Content = capturedKey == 0
                    ? "点击后按下组合键"
                    : HotkeyFormatter.Format(capturedModifiers, capturedKey);
                e.Handled = true;
                return;
            }

            capturedModifiers = CurrentModifiers();
            capturedKey = key;
            capturing = false;
            captureButton.Content = HotkeyFormatter.Format(capturedModifiers, capturedKey);
            e.Handled = true;
        };

        captureButton.PointerPressed += (_, e) =>
        {
            if (!capturing)
                return;
            var point = e.GetCurrentPoint(captureButton);
            var key = point.Properties.IsMiddleButtonPressed ? VirtualKeys.MouseMiddle : 0;
            if (key == 0)
                return;

            capturedModifiers = CurrentModifiers();
            capturedKey = key;
            capturing = false;
            captureButton.Content = HotkeyFormatter.Format(capturedModifiers, capturedKey);
            e.Handled = true;
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            errorText.Visibility = Visibility.Collapsed;
            if (capturedKey == 0)
            {
                errorText.Text = "请先录制一个快捷键组合。";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            if (VirtualKeys.IsMouse(capturedKey) && capturedModifiers == HotkeyModifiers.None)
            {
                errorText.Text = "鼠标键需要搭配 Ctrl、Alt 或 Shift 使用。";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            if (!VirtualKeys.IsMouse(capturedKey) && capturedModifiers == HotkeyModifiers.None &&
                capturedKey is < 0x70 or > 0x87)
            {
                errorText.Text = "请至少添加一个修饰键，或使用 F1–F24。";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            if (actionCombo.SelectedItem is not ActionChoice action)
            {
                args.Cancel = true;
                return;
            }

            var targets = targetList.SelectedItems.OfType<DeviceChoice>().ToList();
            if (HotkeyCatalog.RequiresTarget(action.Action) && targets.Count == 0)
            {
                errorText.Text = "请选择至少一台目标设备。";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var source = sourceCombo.SelectedItem as DeviceChoice;
            var name = nameBox.Text.Trim();
            var binding = new HotkeyBinding
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? action.Name : name,
                Action = action.Action,
                Modifiers = capturedModifiers,
                Key = capturedKey,
                TargetDeviceId = targets.FirstOrDefault()?.Id ?? string.Empty,
                TargetDeviceName = targets.FirstOrDefault()?.DisplayName ?? string.Empty,
                TargetDeviceIds = targets.Select(t => t.Id).ToList(),
                TargetDeviceNames = targets.Select(t => t.DisplayName).ToList(),
                SourceDeviceId = source is null || source.IsAnyDevice ? string.Empty : source.Id,
                SourceDeviceName = source is null || source.IsAnyDevice ? string.Empty : source.DisplayName,
                IsEnabled = enabledSwitch.IsOn
            };
            vm.AddOrUpdateHotkey(binding, isNew: existing is null);
        };

        if (existing is not null)
        {
            existing.EnsureTargets();
            nameBox.Text = existing.Name;
            actionCombo.SelectedItem = actions.FirstOrDefault(a => a.Action == existing.Action);
            sourceCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == existing.SourceDeviceId) ?? choices[0];
            foreach (var choice in targetChoices.Where(c =>
                         existing.TargetDeviceIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase)))
                targetList.SelectedItems.Add(choice);
            captureButton.Content = HotkeyFormatter.Format(existing.Modifiers, existing.Key);
        }
        else
        {
            actionCombo.SelectedIndex = 0;
            sourceCombo.SelectedIndex = 0;
            if (preselectedTargetId is not null)
            {
                var preselected = targetChoices.FirstOrDefault(c => c.Id == preselectedTargetId);
                if (preselected is not null)
                    targetList.SelectedItems.Add(preselected);
                actionCombo.SelectedItem = actions.FirstOrDefault(a => a.Action == HotkeyAction.Takeover);
            }
        }

        if (actionCombo.SelectedItem is ActionChoice initial)
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
                nameBox.Text = initial.Name;
            actionDescription.Text = initial.Description;
            updateTargetVisibility();
        }

        await dialog.ShowAsync();
    }

    private static string BuildPreview(int key)
    {
        var modifiers = CurrentModifiers();
        return (modifiers == HotkeyModifiers.None ? "请按下组合键" : HotkeyFormatter.Format(modifiers, 0))
            .Replace(" + 未设置", " + …");
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var result = HotkeyModifiers.None;
        if (IsDown(0x11)) result |= HotkeyModifiers.Control;
        if (IsDown(0x12)) result |= HotkeyModifiers.Alt;
        if (IsDown(0x10)) result |= HotkeyModifiers.Shift;
        if (IsDown(0x5B) || IsDown(0x5C)) result |= HotkeyModifiers.Win;
        return result;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private sealed class ActionChoice
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

    private sealed class DeviceChoice
    {
        public DeviceChoice(string id, string displayName, bool isAnyDevice)
        {
            Id = id;
            DisplayName = displayName;
            IsAnyDevice = isAnyDevice;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool IsAnyDevice { get; }
        public override string ToString() => DisplayName;
    }
}
