using System.Diagnostics;
using Microsoft.Win32;
using MouseGuard.Native;

namespace MouseGuard.Services;

public sealed class AutoStartService
{
    private const string TaskName = "MouseGuard";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MouseGuard";

    public bool IsElevated => SetupApiInterop.IsAdministrator;

    public bool IsTaskInstalled()
    {
        try
        {
            var result = RunSchtasks("/Query", "/TN", TaskName);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRunKeyInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Contains("MouseGuard", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool IsEnabled => IsTaskInstalled() || IsRunKeyInstalled();

    public string GetStatusText()
    {
        if (IsTaskInstalled())
            return IsElevated ? "已通过计划任务静默启动" : "已通过计划任务启动";
        if (IsRunKeyInstalled())
            return "已通过当前用户启动项启动（标准权限）";
        return "未启用";
    }

    public (bool Success, string Message) Enable(bool preferTask = true)
    {
        var exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return (false, "无法确定程序路径。");

        if (preferTask && IsElevated)
        {
            var create = RunSchtasks(
                "/Create", "/TN", TaskName,
                "/TR", $"\"{exePath}\" --tray",
                "/SC", "ONLOGON",
                "/RL", "HIGHEST",
                "/F");

            if (create.ExitCode == 0)
            {
                RemoveRunKey();
                return (true, "已创建登录时静默启动的计划任务。");
            }

            LogService.Warn($"创建计划任务失败: {create.Output}");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key?.SetValue(RunValueName, $"\"{exePath}\" --tray");
            return (true, IsElevated
                ? "已写入当前用户启动项。"
                : "已启用开机启动。若需要静默的设备控制，请以管理员身份重启并再次开启。");
        }
        catch (Exception ex)
        {
            LogService.Error("写入启动项失败", ex);
            return (false, "写入启动项失败：" + ex.Message);
        }
    }

    public (bool Success, string Message) Disable()
    {
        var taskDeleted = true;
        if (IsElevated)
        {
            var delete = RunSchtasks("/Delete", "/TN", TaskName, "/F");
            taskDeleted = delete.ExitCode == 0 || delete.Output.Contains("不存在", StringComparison.OrdinalIgnoreCase)
                          || delete.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                          || delete.Output.Contains("找不到", StringComparison.OrdinalIgnoreCase);
        }

        var runDeleted = RemoveRunKey();

        if (!taskDeleted)
            return (false, "删除计划任务失败，请以管理员身份重试。");

        return (true, runDeleted || taskDeleted ? "已关闭开机启动。" : "开机启动已关闭。");
    }

    private static bool RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(RunValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("删除启动项失败", ex);
            return false;
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process is null)
            return (-1, "无法启动 schtasks.exe");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(8000);
        return (process.HasExited ? process.ExitCode : -1, output.Trim());
    }
}
