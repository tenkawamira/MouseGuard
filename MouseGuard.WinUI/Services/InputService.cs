using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MouseGuard.Models;
using MouseGuard.Services;

namespace MouseGuard.WinUI.Services;

/// <summary>
/// Native WinUI input plumbing: a window subclass for Raw Input / WM_HOTKEY,
/// RegisterHotKey for system-wide shortcuts, and a low-level mouse hook for
/// the software mouse lock plus mouse-button shortcuts.
/// </summary>
public sealed class InputService : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const int WM_HOTKEY = 0x0312;
    private const int WH_MOUSE_LL = 14;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int EmergencyId = 0x4D47;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private readonly IntPtr _hwnd;
    private readonly Func<string, string?> _resolveDevice;
    private readonly DispatcherQueue _dispatcher;
    private readonly SubclassProc _subclassProc;
    private readonly MouseHookProc _mouseProc;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private readonly Dictionary<string, long> _lastFire = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HotkeyBinding> _keyboardBindings = new();
    private readonly List<HotkeyBinding> _mouseBindings = new();
    private readonly List<string> _failedHotkeys = new();
    private readonly Dictionary<IntPtr, string> _rawNames = new();
    private IntPtr _mouseHook;
    private int _nextId = 100;
    private bool _started;

    public InputService(IntPtr hwnd, Func<string, string?> resolveDevice, DispatcherQueue dispatcher)
    {
        _hwnd = hwnd;
        _resolveDevice = resolveDevice;
        _dispatcher = dispatcher;
        _subclassProc = WindowProc;
        _mouseProc = MouseHook;
    }

    public event EventHandler<HotkeyBinding>? HotkeyTriggered;
    public event EventHandler? PanicReleaseRequested;
    public event EventHandler? DevicesChanged;

    public string? LastMouseDeviceId { get; private set; }
    public string? LastKeyboardDeviceId { get; private set; }
    public IReadOnlyList<string> FailedHotkeys => _failedHotkeys;

    public bool Start()
    {
        if (_started)
            return true;

        if (!SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero))
        {
            LogService.Warn("SetWindowSubclass 失败");
            return false;
        }

        RegisterRawInput();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
        if (_mouseHook == IntPtr.Zero)
            LogService.Warn("鼠标低级钩子安装失败");

        RegisterHotKey(EmergencyId,
            (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift) | MOD_NOREPEAT,
            0x1B);
        _started = true;
        return true;
    }

    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
    {
        var snapshot = bindings.Where(b => b.IsEnabled).Select(b => b.Clone()).ToList();
        _keyboardBindings.Clear();
        _keyboardBindings.AddRange(snapshot.Where(b => !VirtualKeys.IsMouse(b.Key)));
        _mouseBindings.Clear();
        _mouseBindings.AddRange(snapshot.Where(b => VirtualKeys.IsMouse(b.Key)));
        _failedHotkeys.Clear();

        foreach (var id in _registered.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();

        foreach (var binding in _keyboardBindings)
        {
            if (binding.Key == 0x1B && binding.Modifiers ==
                (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift))
                continue;

            var id = _nextId++;
            if (RegisterHotKey(id, (uint)binding.Modifiers | MOD_NOREPEAT, (uint)binding.Key))
            {
                _registered[id] = binding;
            }
            else
            {
                _failedHotkeys.Add($"{binding.GestureText}（{binding.ActionName}）");
                LogService.Warn($"快捷键注册失败: {binding.GestureText}");
            }
        }
    }

    private bool RegisterHotKey(int id, uint modifiers, uint virtualKey)
        => RegisterHotKeyNative(_hwnd, id, modifiers, virtualKey);

    private void RegisterRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = _hwnd
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = _hwnd
            }
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            LogService.Warn("RegisterRawInputDevices 失败");
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        switch (msg)
        {
            case WM_INPUT:
                ReadRawInput(lParam);
                break;
            case WM_INPUT_DEVICE_CHANGE:
                _rawNames.Clear();
                DevicesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WM_HOTKEY:
                HandleHotkey(wParam.ToInt32());
                break;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void HandleHotkey(int id)
    {
        if (id == EmergencyId)
        {
            _dispatcher.TryEnqueue(() => PanicReleaseRequested?.Invoke(this, EventArgs.Empty));
            return;
        }

        if (!_registered.TryGetValue(id, out var binding))
            return;

        if (!IsSourceAllowed(binding) || !TryBeginAction(binding.Id))
            return;

        _dispatcher.TryEnqueue(() => HotkeyTriggered?.Invoke(this, binding));
    }

    private void ReadRawInput(IntPtr hRawInput)
    {
        var size = 0u;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0 || size > 8192)
            return;

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, handle.AddrOfPinnedObject(), ref size,
                    (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(handle.AddrOfPinnedObject());
            var path = GetRawDeviceName(header.hDevice);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var deviceId = _resolveDevice(path);
            if (header.dwType == 0)
            {
                if (deviceId is not null) LastMouseDeviceId = deviceId;
            }
            else if (header.dwType == 1)
            {
                if (deviceId is not null) LastKeyboardDeviceId = deviceId;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private string? GetRawDeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero)
            return null;
        if (_rawNames.TryGetValue(device, out var cached))
            return cached;

        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0 || size > 4096)
            return null;

        var sb = new System.Text.StringBuilder((int)size + 1);
        if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref size) == uint.MaxValue)
            return null;

        var name = sb.ToString();
        if (!string.IsNullOrWhiteSpace(name))
            _rawNames[device] = name;
        return name;
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var key = message switch
        {
            WM_MBUTTONDOWN => VirtualKeys.MouseMiddle,
            WM_XBUTTONDOWN => (info.mouseData >> 16) == 1 ? VirtualKeys.MouseX1 : VirtualKeys.MouseX2,
            _ => 0
        };

        if (key != 0)
        {
            var binding = _mouseBindings.FirstOrDefault(b =>
                b.Key == key && Normalize(b.Modifiers) == CurrentModifiers());
            if (binding is not null)
            {
                if (IsSourceAllowed(binding) && TryBeginAction(binding.Id))
                    _dispatcher.TryEnqueue(() => HotkeyTriggered?.Invoke(this, binding));
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsSourceAllowed(HotkeyBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.SourceDeviceId))
            return true;

        var current = VirtualKeys.IsMouse(binding.Key) ? LastMouseDeviceId : LastKeyboardDeviceId;
        return current is null || string.Equals(current, binding.SourceDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBeginAction(string id)
    {
        var now = Environment.TickCount64;
        if (_lastFire.TryGetValue(id, out var last) && now - last < 550)
            return false;
        _lastFire[id] = now;
        return true;
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

    private static HotkeyModifiers Normalize(HotkeyModifiers modifiers) => modifiers;
    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        UnregisterHotKey(_hwnd, EmergencyId);

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, 1);
        _started = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);
    private delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, System.Text.StringBuilder data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    private static extern bool RegisterHotKeyNative(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
