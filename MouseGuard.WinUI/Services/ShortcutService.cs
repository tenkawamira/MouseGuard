using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MouseGuard.Services;

namespace MouseGuard.WinUI.Services;

/// <summary>
/// Unpackaged notification support needs a Start-menu shortcut carrying an
/// AppUserModelID. The portable build creates/updates it on demand.
/// </summary>
internal static class ShortcutService
{
    public const string AppUserModelId = "MouseGuard.Desktop";

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly PROPERTYKEY AppUserModelIdKey = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    public static bool EnsureShortcut()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return false;

            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(programs);
            var shortcutPath = Path.Combine(programs, "MouseGuard.lnk");

            var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClsid);
            if (shellLinkType is null)
                return false;
            var instance = Activator.CreateInstance(shellLinkType);
            if (instance is not IShellLinkW link)
                return false;

            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
            link.SetDescription("鼠标卫士 MouseGuard");
            link.SetIconLocation(exe, 0);

            if (instance is IPropertyStore store)
            {
                var value = new PROPVARIANT { vt = 31, pointerValue = Marshal.StringToCoTaskMemUni(AppUserModelId) };
                try
                {
                    var key = AppUserModelIdKey;
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(value.pointerValue);
                }
            }

            if (instance is IPersistFile persistFile)
                persistFile.Save(shortcutPath, true);

            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("创建通知快捷方式失败: " + ex.Message);
            return false;
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
