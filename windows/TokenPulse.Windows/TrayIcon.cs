using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TokenPulse.Windows;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon notify;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Icon normal = CreateIcon(false);
    private readonly Icon warning = CreateIcon(true);
    public TrayIcon(Action show, Action refresh, Action settings, Action exit)
    {
        menu.Items.Add("打开面板", null, (_, _) => show());
        menu.Items.Add("立即刷新", null, (_, _) => refresh());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        // NotifyIcon handles the shell's TaskbarCreated message and restores itself after Explorer restarts.
        notify = new Forms.NotifyIcon { Icon = normal, Text = "TokenPulse · 正在读取本地日志", ContextMenuStrip = menu, Visible = true };
        notify.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }
    public void Update(DashboardSnapshot today)
    {
        notify.Icon = today.IsHighBurnRate ? warning : normal;
        var text = $"TokenPulse · 今日 {Formatting.Tokens(today.Totals.Total)} · {Formatting.Usd(today.Totals.Cost)}";
        notify.Text = text.Length <= 63 ? text : text[..63];
    }
    private static Icon CreateIcon(bool alert)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new SolidBrush(alert ? System.Drawing.Color.FromArgb(234, 88, 12) : System.Drawing.Color.FromArgb(37, 99, 235));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        if (alert)
        {
            graphics.FillRectangle(System.Drawing.Brushes.White, 14, 7, 4, 12);
            graphics.FillEllipse(System.Drawing.Brushes.White, 14, 22, 4, 4);
        }
        else graphics.FillPolygon(System.Drawing.Brushes.White, new System.Drawing.Point[] { new(18, 5), new(9, 18), new(15, 18), new(13, 27), new(24, 13), new(18, 13) });
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose()
    {
        notify.Visible = false;
        notify.Dispose(); menu.Dispose(); normal.Dispose(); warning.Dispose();
    }
}
