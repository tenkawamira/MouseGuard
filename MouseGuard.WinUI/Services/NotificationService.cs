using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using MouseGuard.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MouseGuard.WinUI.Services;

/// <summary>
/// Native Windows toast notifications for the portable build. Windows requires
/// a Start-menu shortcut with an AppUserModelID for unpackaged notification
/// delivery; ShortcutService maintains that shortcut on demand.
/// </summary>
public static class NotificationService
{
    private static bool _registered;
    private static bool _registrationFailed;

    /// <summary>
    /// Last-resort native notification-area balloon, wired to the tray icon.
    /// It also works in restricted environments where the Start-menu shortcut
    /// required by Action Center toasts cannot be written.
    /// </summary>
    public static Action<string, string>? BalloonFallback { get; set; }

    public static bool Show(string title, string message)
    {
        var heading = string.IsNullOrWhiteSpace(title) ? "鼠标已被接管" : title;
        if (TryAppNotification(heading, message))
            return true;

        if (TryToastNotification(heading, message))
            return true;

        if (BalloonFallback is not null)
        {
            BalloonFallback(heading, message ?? string.Empty);
            return true;
        }

        return false;
    }

    private static bool TryAppNotification(string title, string message)
    {
        if (!_registered)
        {
            if (_registrationFailed || !ShortcutService.EnsureShortcut())
                return false;

            try
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            catch (Exception ex)
            {
                _registrationFailed = true;
                LogService.Warn("AppNotification 注册失败: " + ex.Message);
                return false;
            }
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message ?? string.Empty)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("AppNotification 发送失败: " + ex.Message);
            return false;
        }
    }

    private static bool TryToastNotification(string title, string message)
    {
        try
        {
            if (!ShortcutService.EnsureShortcut())
                return false;

            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            if (texts.Count < 2)
                return false;
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(message ?? string.Empty));

            var notifier = ToastNotificationManager.CreateToastNotifier(ShortcutService.AppUserModelId);
            notifier.Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("系统 Toast 发送失败: " + ex.Message);
            return false;
        }
    }
}
