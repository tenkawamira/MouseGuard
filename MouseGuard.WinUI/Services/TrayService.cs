using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseGuard.WinUI.Services;

/// <summary>
/// Native Shell_NotifyIcon tray integration. Keeps the app out of the
/// taskbar while the main window is hidden.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint TrayMessage = WM_APP + 1;
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint IDI_APPLICATION = 32512;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _subclassProc;
    private readonly uint _id = 0x4D47;
    private IntPtr _icon;
    private bool _added;

    public TrayService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _subclassProc = WindowProc;
        SetWindowSubclass(_hwnd, _subclassProc, 2, IntPtr.Zero);
        _icon = ExtractIcon();
        AddIcon();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ReleaseAllRequested;
    public event EventHandler? ShowPopupRequested;
    public event EventHandler? ExitRequested;

    public void ShowBalloon(string title, string text)
    {
        if (!_added)
            return;

        var data = CreateData();
        data.uFlags |= NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = 0x1;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.szTip = "鼠标卫士";
        if (Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _added = true;
            var version = CreateData();
            version.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref version);
        }
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = _id,
        uCallbackMessage = TrayMessage
    };

    private IntPtr ExtractIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) &&
                ExtractIconEx(path, 0, out _, out var small, 1) > 0 && small != IntPtr.Zero)
                return small;
        }
        catch
        {
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == TrayMessage)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is WM_CONTEXTMENU or WM_LBUTTONDBLCLK or NIN_SELECT)
                ShowMenu();
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MF_STRING, 100, "打开控制台");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 102, "紧急释放全部控制");
            AppendMenu(menu, MF_STRING, 103, "显示接管提示");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 104, "退出鼠标卫士");

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case 100: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case 102: ReleaseAllRequested?.Invoke(this, EventArgs.Empty); break;
                case 103: ShowPopupRequested?.Invoke(this, EventArgs.Empty); break;
                case 104: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, 2);
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
