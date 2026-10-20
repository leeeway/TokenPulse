using System.ComponentModel;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace TokenPulse.Windows;

internal sealed class MainWindow : Window
{
    private readonly TextBlock headline = Text("正在读取本地日志…", 26, FontWeights.Bold);
    private readonly TextBlock details = Text("");
    private readonly TextBlock alert = Text("", 13, FontWeights.SemiBold);
    private readonly TextBlock status = Text("", 11);
    private readonly TextBlock error = Text("", 12);
    private readonly StackPanel providers = new();
    private readonly System.Windows.Controls.Primitives.UniformGrid trend = new() { Rows = 1, Columns = 7 };
    private readonly TextBlock localModels = Text("", 12);
    private readonly ComboBox windowPicker = new() { MinWidth = 115, Margin = new Thickness(0, 0, 10, 0) };
    public TimeWindow SelectedWindow => (TimeWindow)Math.Max(0, windowPicker.SelectedIndex);
    public bool AllowClose { get; set; }

    public MainWindow(Action refresh, Action settings, Action changeWindow)
    {
        Title = "TokenPulse";
        Width = 540; Height = 720; MinWidth = 440; MinHeight = 420;
        ShowInTaskbar = false;
        Background = Brush("#F5F7FB"); Foreground = Brush("#172033");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(Text("⚡ TokenPulse", 24, FontWeights.Bold));
        header.Children.Add(Text("本地 AI 用量 · Windows", 12));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 16) };
        foreach (var title in new[] { "今日", "近 7 天", "近 30 天", "全部" }) windowPicker.Items.Add(title);
        windowPicker.SelectedIndex = 0;
        windowPicker.SelectionChanged += (_, _) => changeWindow();
        controls.Children.Add(windowPicker);
        controls.Children.Add(Button("刷新", refresh));
        controls.Children.Add(Button("设置", settings));
        header.Children.Add(controls);
        header.Children.Add(error);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = Text("只读本地日志 · 费用为估算值 · 关闭窗口后继续驻留托盘", 11);
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(alert);
        var metrics = new StackPanel(); metrics.Children.Add(headline); metrics.Children.Add(details);
        content.Children.Add(Card(metrics));
        content.Children.Add(Text("AI 产品明细", 15, FontWeights.SemiBold));
        content.Children.Add(providers);
        content.Children.Add(Text("近 7 天 Token 趋势", 15, FontWeights.SemiBold));
        content.Children.Add(Card(trend));
        content.Children.Add(localModels);
        content.Children.Add(Text("数据源状态", 15, FontWeights.SemiBold));
        content.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Closing += OnClosing;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
    }
    public void OpenNearTray()
    {
        if (IsVisible) { Activate(); return; }
        Show();
        // Keep placement in device pixels: absolute DIP coordinates are ambiguous across mixed-DPI monitors.
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var area = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).Handle;
        // First move to the chosen monitor so PerMonitorV2 updates the window's DPI before DIP placement.
        NativePosition.SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, 0, 0, 0x0015);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width - 16, area.Height - 16));
        MaxHeight = Math.Max(MinHeight, available.Y);
        MaxWidth = Math.Max(MinWidth, available.X);
        UpdateLayout();
        if (NativePosition.GetWindowRect(handle, out var bounds))
        {
            var width = Math.Min(bounds.Right - bounds.Left, area.Width - 16);
            var height = Math.Min(bounds.Bottom - bounds.Top, area.Height - 16);
            NativePosition.SetWindowPos(handle, IntPtr.Zero, area.Right - width - 8, area.Bottom - height - 8, width, height, 0x0014);
        }
        Activate();
    }
    public void ShowError(string? message) => error.Text = message ?? "";
    public void Render(DashboardSnapshot snapshot, ScanResult scan, OllamaResult ollama)
    {
        var t = snapshot.Totals;
        headline.Text = $"{Formatting.Tokens(t.Total)} Token";
        details.Text = $"↑ {Formatting.Tokens(t.Upstream)}    ↓ {Formatting.Tokens(t.Output)}\n"
            + $"缓存命中 {t.CacheHitRatio:P1} · 等效 API {Formatting.Usd(t.Cost)} · 缓存节省 {Formatting.Usd(t.Saved)}\n"
            + $"{t.Sessions} 个会话 · {t.Events} 条记录 · 推理 {Formatting.Tokens(t.Reasoning)}（已含于输出）";
        alert.Text = snapshot.IsHighBurnRate ? $"⚠ 过去 5 分钟消耗 {Formatting.Tokens(snapshot.Recent.Total)} Token / {Formatting.Usd(snapshot.Recent.Cost)}" : "";
        alert.Foreground = Brush("#C2410C");
        providers.Children.Clear();
        if (snapshot.Providers.Count == 0) providers.Children.Add(Text("当前时间范围暂无用量记录，可切换统计周期或检查数据源。"));
        foreach (var p in snapshot.Providers)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text($"{Providers.Name(p.Provider)}    {Formatting.Tokens(p.Totals.Total)} · {Formatting.Usd(p.Totals.Cost)}", 14, FontWeights.SemiBold));
            panel.Children.Add(Text($"↑ {Formatting.Tokens(p.Totals.Upstream)}  ↓ {Formatting.Tokens(p.Totals.Output)}  缓存 {p.Totals.CacheHitRatio:P1}\n{string.Join(" · ", p.TopModels)}", 12));
            if (p.RateLimit is { } quota)
            {
                AddQuota(panel, quota.Primary);
                if (quota.Secondary is { } secondary) AddQuota(panel, secondary);
                panel.Children.Add(Text($"配额记录：{quota.UpdatedAt.LocalDateTime:g} · {quota.PlanType ?? "未知套餐"}", 10));
            }
            providers.Children.Add(Card(panel));
        }
        trend.Children.Clear();
        var max = Math.Max(1, snapshot.Trend.Max(p => p.Total));
        foreach (var day in snapshot.Trend)
        {
            var column = new StackPanel { Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Bottom };
            column.Children.Add(Text(Formatting.Tokens(day.Total), 10));
            column.Children.Add(new Border { Height = Math.Max(3, 60.0 * day.Total / max), Width = 26,
                Background = Brush("#3B82F6"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"↑ {day.Upstream:N0} / ↓ {day.Output:N0} · {Formatting.Usd(day.Cost)}" });
            column.Children.Add(Text(day.Date.ToString("MM/dd"), 10));
            trend.Children.Add(column);
        }
        localModels.Text = ollama.Status + string.Concat(ollama.Models.Select(m => $"\n{m.Name} · 显存 {m.VramBytes / 1073741824.0:F1} GiB"));
        status.Text = string.Join("\n\n", scan.Statuses.Select(s => $"{Providers.Name(s.Provider)} · {s.Message}\n{s.Path}"))
            + $"\n\n更新于 {snapshot.GeneratedAt.LocalDateTime:T}";
    }
    private static void AddQuota(Panel panel, QuotaWindow quota)
    {
        var duration = quota.WindowMinutes is { } minutes ? minutes % 1440 == 0 ? $"{minutes / 1440} 天" : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟" : "窗口";
        panel.Children.Add(Text($"{duration}配额已用 {quota.UsedPercent:F1}%" + (quota.ResetsAt is { } reset ? $" · {reset.LocalDateTime:g} 重置" : ""), 11));
        panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(quota.UsedPercent, 0, 100), Height = 5, Margin = new Thickness(0, 2, 0, 4) });
    }
    internal static TextBlock Text(string value, double size = 13, FontWeight? weight = null) => new()
    { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static Border Card(UIElement content) => new() { Child = content, Background = Brushes.White, Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 12), CornerRadius = new CornerRadius(9) };
    private static class NativePosition
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
