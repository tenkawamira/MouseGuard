using System.Drawing;
using Forms = System.Windows.Forms;

namespace MouseGuard.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _miceItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Icon _icon;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleMiceRequested;
    public event EventHandler? ReleaseAllRequested;
    public event EventHandler? ShowPopupRequested;
    public event EventHandler? ExitRequested;

    public TrayService(bool startWithWindows, bool miceLocked)
    {
        _icon = LoadIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "鼠标卫士 MouseGuard",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(28, 32, 41),
            ForeColor = Color.FromArgb(242, 245, 250),
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Padding = new Forms.Padding(4)
        };

        var open = new Forms.ToolStripMenuItem("打开控制台", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };

        _miceItem = new Forms.ToolStripMenuItem("接管全部鼠标", null, (_, _) => ToggleMiceRequested?.Invoke(this, EventArgs.Empty))
        {
            Checked = miceLocked,
            CheckOnClick = false
        };

        var release = new Forms.ToolStripMenuItem("释放全部控制", null, (_, _) => ReleaseAllRequested?.Invoke(this, EventArgs.Empty));
        var popup = new Forms.ToolStripMenuItem("显示接管提示", null, (_, _) => ShowPopupRequested?.Invoke(this, EventArgs.Empty));
        _startupItem = new Forms.ToolStripMenuItem("开机自动启动", null, (_, _) => { })
        {
            Checked = startWithWindows,
            Enabled = false
        };

        var exit = new Forms.ToolStripMenuItem("退出鼠标卫士", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_miceItem);
        menu.Items.Add(release);
        menu.Items.Add(popup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(exit);

        menu.Opening += (_, _) =>
        {
            _miceItem.Checked = miceLocked;
            menu.BackColor = Color.FromArgb(28, 32, 41);
        };

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void SetMiceLocked(bool locked)
    {
        _miceItem.Checked = locked;
        _miceItem.Text = locked ? "释放全部鼠标" : "接管全部鼠标";
    }

    public void SetStartup(bool enabled) => _startupItem.Checked = enabled;

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(3500);
        }
        catch
        {
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(28, 32, 41));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
                return;

            using var brush = new SolidBrush(Color.FromArgb(48, 55, 72));
            var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(242, 245, 250) : Color.FromArgb(120, 130, 145);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(45, 52, 66));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }
}
