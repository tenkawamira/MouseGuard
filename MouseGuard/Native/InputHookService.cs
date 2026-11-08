using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using MouseGuard.Models;
using MouseGuard.Services;

namespace MouseGuard.Native;

/// <summary>
/// Global low-level input hooks. Used for software-level takeover and for
/// device-aware shortcut dispatch (the low-level hooks run before the input
/// reaches the foreground application).
/// </summary>
public sealed class InputHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_ESCAPE = 0x1B;
    private const uint LLKHF_INJECTED = 0x10;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int EmergencyHotkeyId = 0x4D47;

    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly HashSet<int> _suppressedKeys = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly Func<string?, string?> _resolveSource;
    private readonly HwndSource? _hotkeySource;
    private readonly Dictionary<int, HotkeyBinding> _registeredHotkeys = new();
    private readonly Dictionary<string, long> _lastActionTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HotkeyBinding> _mouseBindings = new();
    private readonly List<HotkeyBinding> _keyboardBindings = new();
    private readonly List<string> _failedHotkeys = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private int _nextHotkeyId = 100;
    private volatile bool _miceBlocked;
    private volatile bool _keyboardsBlocked;
    private bool _panicLatch;
    private HotkeyModifiers _modifierState;

    public event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;
    public event EventHandler? PanicReleaseRequested;

    public bool MiceBlocked
    {
        get => _miceBlocked;
        set => _miceBlocked = value;
    }

    public bool KeyboardsBlocked
    {
        get => _keyboardsBlocked;
        set => _keyboardsBlocked = value;
    }

    public string? LastTriggeredDeviceId { get; private set; }

    public int RegisteredHotkeyCount => _registeredHotkeys.Count;
    public int RequestedKeyboardHotkeyCount => _keyboardBindings.Count;
    public IReadOnlyList<string> FailedHotkeys => _failedHotkeys;

    public InputHookService(Func<string?, string?> resolveSource)
    {
        _resolveSource = resolveSource;
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;

        try
        {
            var parameters = new HwndSourceParameters("MouseGuard.HotkeySink")
            {
                ParentWindow = IntPtr.Zero,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ExtendedWindowStyle = 0x00000080,          // WS_EX_TOOLWINDOW
                Width = 1,
                Height = 1
            };
            _hotkeySource = new HwndSource(parameters);
            _hotkeySource.AddHook(HotkeyWndProc);
            ShowWindow(_hotkeySource.Handle, 0);
            RegisterEmergencyHotkey();
        }
        catch (Exception ex)
        {
            LogService.Error("创建快捷键消息窗口失败", ex);
        }
    }

    public bool Start()
    {
        if (_keyboardHook != IntPtr.Zero)
            return true;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero)
            LogService.Warn("键盘低级钩子安装失败，快捷键与键盘拦截不可用。");
        if (_mouseHook == IntPtr.Zero)
            LogService.Warn("鼠标低级钩子安装失败，全局鼠标拦截不可用。");
        return _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
    }

    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
    {
        var snapshot = bindings.Where(b => b.IsEnabled).Select(b => b.Clone()).ToList();
        _mouseBindings.Clear();
        _mouseBindings.AddRange(snapshot.Where(b => VirtualKeys.IsMouse(b.Key)));
        _keyboardBindings.Clear();
        _keyboardBindings.AddRange(snapshot.Where(b => !VirtualKeys.IsMouse(b.Key)));
        _failedHotkeys.Clear();

        if (_hotkeySource is null)
            return;

        foreach (var id in _registeredHotkeys.Keys.ToList())
            UnregisterHotKey(_hotkeySource.Handle, id);
        _registeredHotkeys.Clear();

        foreach (var binding in snapshot.Where(b => !VirtualKeys.IsMouse(b.Key)))
        {
            if (binding.Key == VK_ESCAPE &&
                binding.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift))
                continue;

            var id = _nextHotkeyId++;
            if (RegisterHotKey(_hotkeySource.Handle, id, (uint)binding.Modifiers | MOD_NOREPEAT, (uint)binding.Key))
            {
                _registeredHotkeys[id] = binding;
            }
            else
            {
                _failedHotkeys.Add($"{binding.GestureText}（{binding.ActionName}）");
                LogService.Warn($"快捷键注册失败（可能被占用）: {binding.GestureText} / {binding.ActionName}");
            }
        }

        LogService.Info($"已注册快捷键: {_registeredHotkeys.Count} / {snapshot.Count(b => !VirtualKeys.IsMouse(b.Key))}");
    }

    private void RegisterEmergencyHotkey()
    {
        if (_hotkeySource is null)
            return;

        var ok = RegisterHotKey(_hotkeySource.Handle, EmergencyHotkeyId,
            (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift) | MOD_NOREPEAT,
            VK_ESCAPE);
        if (!ok)
            LogService.Warn("紧急释放组合键注册失败，将使用低级键盘钩子兜底。");
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
            return IntPtr.Zero;

        var id = wParam.ToInt32();
        handled = true;

        if (id == EmergencyHotkeyId)
        {
            PanicReleaseRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (!_registeredHotkeys.TryGetValue(id, out var binding))
            return IntPtr.Zero;

        if (IsSourceAllowed(binding) && TryBeginAction(binding.Id))
        {
            LastTriggeredDeviceId = _resolveSource("__LAST_KEYBOARD__");
            HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(binding, LastTriggeredDeviceId));
        }

        return IntPtr.Zero;
    }

    public void Stop()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var key = (int)info.vkCode;

        if (message is WM_KEYUP or WM_SYSKEYUP)
        {
            UpdateModifierState(key, down: false);
            _pressedKeys.Remove(key);
            if (_suppressedKeys.Remove(key))
                return new IntPtr(1);
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        UpdateModifierState(key, down: true);
        _pressedKeys.Add(key);

        // Failsafe: Ctrl + Alt + Shift + Esc always releases every takeover.
        if (key == VK_ESCAPE && CurrentModifiers().HasFlag(HotkeyModifiers.Control) &&
            CurrentModifiers().HasFlag(HotkeyModifiers.Alt) &&
            CurrentModifiers().HasFlag(HotkeyModifiers.Shift))
        {
            if (!_panicLatch)
            {
                _panicLatch = true;
                PanicReleaseRequested?.Invoke(this, EventArgs.Empty);
            }

            if (!_suppressedKeys.Contains(key))
                _suppressedKeys.Add(key);
            return new IntPtr(1);
        }

        if (key != VK_ESCAPE)
            _panicLatch = false;

        if (_keyboardsBlocked)
        {
            if (!_suppressedKeys.Contains(key))
                _suppressedKeys.Add(key);
            return new IntPtr(1);
        }

        if (!IsModifierKey(key))
        {
            var binding = FindKeyboardBinding(key);
            if (binding is not null)
            {
                _suppressedKeys.Add(key);
                if (IsSourceAllowed(binding) && TryBeginAction(binding.Id))
                {
                    LastTriggeredDeviceId = _resolveSource("__LAST_KEYBOARD__");
                    HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(binding, LastTriggeredDeviceId));
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private HotkeyBinding? FindKeyboardBinding(int key)
    {
        var modifiers = CurrentModifiers();
        foreach (var binding in _keyboardBindings)
        {
            if (binding.Key != key)
                continue;
            if (NormalizeModifiers(binding.Modifiers) == modifiers)
                return binding;
        }

        return null;
    }

    private bool TryBeginAction(string id)
    {
        var now = Environment.TickCount64;
        if (_lastActionTimes.TryGetValue(id, out var last) && now - last < 550)
            return false;

        _lastActionTimes[id] = now;
        return true;
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

        if (_miceBlocked)
            return new IntPtr(1);

        var key = message switch
        {
            WM_LBUTTONDOWN => VirtualKeys.MouseLeft,
            WM_RBUTTONDOWN => VirtualKeys.MouseRight,
            WM_MBUTTONDOWN => VirtualKeys.MouseMiddle,
            WM_XBUTTONDOWN => (info.mouseData >> 16) == 1 ? VirtualKeys.MouseX1 : VirtualKeys.MouseX2,
            _ => 0
        };

        if (key != 0)
        {
            var binding = FindMouseBinding(key);
            if (binding is not null)
            {
                if (IsSourceAllowed(binding) && TryBeginAction(binding.Id))
                {
                    LastTriggeredDeviceId = _resolveSource("__LAST_MOUSE__");
                    HotkeyTriggered?.Invoke(this, new HotkeyTriggeredEventArgs(binding, LastTriggeredDeviceId));
                }

                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private HotkeyBinding? FindMouseBinding(int key)
    {
        var modifiers = CurrentModifiers();
        foreach (var binding in _mouseBindings)
        {
            if (binding.Key != key)
                continue;
            if (NormalizeModifiers(binding.Modifiers) == modifiers)
                return binding;
        }

        return null;
    }

    private bool IsSourceAllowed(HotkeyBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.SourceDeviceId))
            return true;

        var current = VirtualKeys.IsMouse(binding.Key)
            ? _resolveSource("__LAST_MOUSE__")
            : _resolveSource("__LAST_KEYBOARD__");

        // If the most recent device cannot be resolved yet (e.g. the very first
        // keystroke after startup), do not make the shortcut feel broken.
        return current is null || string.Equals(current, binding.SourceDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private HotkeyModifiers CurrentModifiers()
    {
        // The modifier keys are tracked from the hook stream itself. This is
        // more reliable than GetAsyncKeyState for synthetically injected keys
        // and for the very first event after a focus change.
        var modifiers = _modifierState;
        if (IsDown(VK_CONTROL)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(VK_MENU)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private void UpdateModifierState(int key, bool down)
    {
        var flag = key switch
        {
            VK_CONTROL or 0xA2 or 0xA3 => HotkeyModifiers.Control,
            VK_MENU or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            VK_SHIFT or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            VK_LWIN or VK_RWIN => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };

        if (flag == HotkeyModifiers.None)
            return;

        if (down)
            _modifierState |= flag;
        else
            _modifierState &= ~flag;
    }

    private static HotkeyModifiers NormalizeModifiers(HotkeyModifiers modifiers)
    {
        return modifiers;
    }

    private static bool IsModifierKey(int key)
        => key is VK_CONTROL or 0xA2 or 0xA3 or VK_SHIFT or 0xA0 or 0xA1 or VK_MENU or 0xA4 or 0xA5
            or VK_LWIN or VK_RWIN;

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        Stop();

        if (_hotkeySource is not null)
        {
            foreach (var id in _registeredHotkeys.Keys)
                UnregisterHotKey(_hotkeySource.Handle, id);
            _registeredHotkeys.Clear();
            UnregisterHotKey(_hotkeySource.Handle, EmergencyHotkeyId);
            _hotkeySource.RemoveHook(HotkeyWndProc);
            _hotkeySource.Dispose();
        }
    }

    public sealed class HotkeyTriggeredEventArgs : EventArgs
    {
        public HotkeyTriggeredEventArgs(HotkeyBinding binding, string? sourceDeviceId)
        {
            Binding = binding;
            SourceDeviceId = sourceDeviceId;
        }

        public HotkeyBinding Binding { get; }
        public string? SourceDeviceId { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NativeMethods.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
