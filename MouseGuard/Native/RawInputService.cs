using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace MouseGuard.Native;

public sealed class RawInputService : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const int HWND_MESSAGE = -3;

    private readonly HwndSource _source;
    private readonly Dictionary<IntPtr, string> _nameCache = new();
    private readonly Func<string, string?> _resolver;

    public event EventHandler? DevicesChanged;

    public string? LastMouseRawPath { get; private set; }
    public string? LastKeyboardRawPath { get; private set; }
    public string? LastMouseDeviceId { get; private set; }
    public string? LastKeyboardDeviceId { get; private set; }
    public DateTime LastActivityAt { get; private set; } = DateTime.MinValue;

    public RawInputService(Func<string, string?> resolver)
    {
        _resolver = resolver;

        var parameters = new HwndSourceParameters("MouseGuard.RawInput")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        Register();
    }

    private void Register()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = _source.Handle
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget = _source.Handle
            }
        };

        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_INPUT:
                ReadInput(lParam);
                break;
            case WM_INPUT_DEVICE_CHANGE:
                _nameCache.Clear();
                DevicesChanged?.Invoke(this, EventArgs.Empty);
                break;
        }

        return IntPtr.Zero;
    }

    private void ReadInput(IntPtr hRawInput)
    {
        var size = 0u;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0 || size > 8192)
            return;

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var result = GetRawInputData(hRawInput, RID_INPUT, handle.AddrOfPinnedObject(), ref size,
                (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (result == uint.MaxValue)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(handle.AddrOfPinnedObject());
            var rawPath = GetDeviceName(header.hDevice);
            if (string.IsNullOrWhiteSpace(rawPath))
                return;

            LastActivityAt = DateTime.Now;
            var resolved = _resolver(rawPath);

            if (header.dwType == 0)
            {
                LastMouseRawPath = rawPath;
                if (resolved is not null)
                    LastMouseDeviceId = resolved;
            }
            else if (header.dwType == 1)
            {
                LastKeyboardRawPath = rawPath;
                if (resolved is not null)
                    LastKeyboardDeviceId = resolved;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private string? GetDeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero)
            return null;
        if (_nameCache.TryGetValue(device, out var cached))
            return cached;

        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0 || size > 4096)
            return null;

        var sb = new StringBuilder((int)size + 1);
        var result = GetRawInputDeviceInfo(device, RIDI_DEVICENAME, sb, ref size);
        if (result == uint.MaxValue)
            return null;

        var name = sb.ToString();
        if (!string.IsNullOrWhiteSpace(name))
            _nameCache[device] = name;
        return name;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, byte[] pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, StringBuilder pData, ref uint pcbSize);
}
