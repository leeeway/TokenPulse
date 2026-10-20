using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace TokenPulse.Windows;

internal sealed class MainWindow : Window
{
    private readonly TextBlock tokens = Ui.Text("—", 25, Ui.Accent, FontWeights.SemiBold);
    private readonly TextBlock cache = Ui.Text("—", 25, Ui.Green, FontWeights.SemiBold);
    private readonly TextBlock cost = Ui.Text("—", 25, Ui.Amber, FontWeights.SemiBold);
    private readonly TextBlock sessions = Ui.Text("正在读取", 10, Ui.Muted);
    private readonly TextBlock saved = Ui.Text("缓存节省", 10, Ui.Muted);
    private readonly TextBlock upstream = Ui.Text("↑ —", 12, Ui.Blue, FontWeights.SemiBold);
    private readonly TextBlock downstream = Ui.Text("↓ —", 12, Ui.Accent, FontWeights.SemiBold);
    private readonly TextBlock flowDetail = Ui.Text("只读采集本地日志", 10.5, Ui.Muted);
    private readonly Grid flow = Ui.Columns(new(1, GridUnitType.Star), new(0), new(0));
    private readonly TextBlock alertText = Ui.Text("", 10.5, Ui.Amber);
    private readonly TextBlock errorText = Ui.Text("", 11, Ui.Brush("#B44C48"));
    private readonly Border alertBox, errorBox, localModelCard;
    private readonly TextBlock providerCount = Ui.Text("", 10.5, Ui.Muted);
    private readonly StackPanel providerRows = new();
    private readonly System.Windows.Controls.Primitives.UniformGrid trend = new() { Rows = 1, Columns = 7 };
    private readonly TextBlock trendTotal = Ui.Text("", 10.5, Ui.Muted);
    private readonly StackPanel sourceRows = new();
    private readonly TextBlock sourceTitle = Ui.Text("数据源", 11, Ui.Muted);
    private readonly TextBlock updated = Ui.Text("正在连接本地日志", 10, Ui.Muted);
    private readonly TextBlock sourceChevron = Ui.Glyph("\uE70D", 9);
    private readonly TextBlock connection = Ui.Text("本地只读", 10, Ui.Muted);
    private readonly StackPanel localModelRows = new();
    private readonly Button refreshButton;
    private bool sourcesExpanded;
    public TimeWindow SelectedWindow { get; private set; } = TimeWindow.Today;
    public bool AllowClose { get; set; }

    public MainWindow(Action refresh, Action settings, Action changeWindow)
    {
        Title = "TokenPulse"; Width = 468; Height = 740; MinWidth = 420; MinHeight = 500;
        ShowInTaskbar = false; Ui.ConfigureWindow(this);
        using (var icon = TrayIcon.CreateIcon(false)) Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var root = new DockPanel { Margin = new Thickness(19, 18, 19, 0) };
        var top = new StackPanel();
        refreshButton = Ui.IconButton("\uE72C", "刷新本地日志", refresh);
        top.Children.Add(Ui.Header(this, "TokenPulse", "你的 AI 用量，一眼掌握", refreshButton, Ui.IconButton("\uE713", "偏好设置", settings)));
        var segments = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 4 };
        string[] titles = ["今日", "近 7 天", "近 30 天", "全部"];
        for (var index = 0; index < titles.Length; index++)
        {
            var value = (TimeWindow)index;
            var segment = new RadioButton { Content = titles[index], GroupName = "Window", IsChecked = index == 0, Style = (Style)FindResource("Segment") };
            segment.Checked += (_, _) => { SelectedWindow = value; changeWindow(); };
            segments.Children.Add(segment);
        }
        top.Children.Add(new Border { Child = segments, CornerRadius = new CornerRadius(9), Background = Ui.Brush("#EEEFF6"), Padding = new Thickness(2), Margin = new Thickness(0, 17, 0, 14) });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var footer = Ui.Columns(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        footer.Margin = new Thickness(1, 0, 1, 0); footer.Height = 39;
        var privacy = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        privacy.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = Ui.Green, Margin = new Thickness(0, 0, 6, 0) });
        privacy.Children.Add(connection); Ui.Put(footer, privacy, 0);
        updated.HorizontalAlignment = HorizontalAlignment.Right; Ui.Put(footer, updated, 2);
        footer.ToolTip = "只读本地日志 · 费用为兼容估算 · 关闭面板后继续驻留托盘";
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        errorBox = new Border { Child = errorText, Background = Ui.Brush("#FFF0EF"), Padding = new Thickness(11), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        body.Children.Add(errorBox);
        var alertContent = Ui.Columns(GridLength.Auto, new(1, GridUnitType.Star));
        Ui.Put(alertContent, new Border { Child = Ui.Glyph("\uE945", 15, Ui.Amber), Width = 30, VerticalAlignment = VerticalAlignment.Center }, 0);
        var alertLabels = new StackPanel(); alertLabels.Children.Add(Ui.Text("消耗速度较高", 11, Ui.Amber, FontWeights.SemiBold));
        alertText.Margin = new Thickness(0, 3, 0, 0); alertLabels.Children.Add(alertText); Ui.Put(alertContent, alertLabels, 1);
        alertBox = new Border { Child = alertContent, Background = Ui.Brush("#FFF6E8"), BorderBrush = Ui.Brush("#F4E6CD"), BorderThickness = new Thickness(1), Padding = new Thickness(9), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        body.Children.Add(alertBox);
        var metrics = Ui.Columns(new(1, GridUnitType.Star), new(8), new(1, GridUnitType.Star), new(8), new(1, GridUnitType.Star));
        Border Metric(string label, TextBlock value, TextBlock caption)
        {
            var stack = new StackPanel(); stack.Children.Add(Ui.Text(label, 10.5, Ui.Muted));
            value.TextWrapping = TextWrapping.NoWrap; value.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            stack.Children.Add(new Viewbox { Child = value, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, Height = 34, Margin = new Thickness(0, 5, 0, 4) });
            caption.TextWrapping = TextWrapping.NoWrap; caption.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(caption);
            return Ui.Card(stack, new Thickness(11, 12, 9, 12));
        }
        Ui.Put(metrics, Metric("总 Token", tokens, sessions), 0); Ui.Put(metrics, Metric("缓存命中", cache, saved), 2);
        Ui.Put(metrics, Metric("API 价值", cost, Ui.Text("等效费用估算", 10, Ui.Muted)), 4);
        body.Children.Add(metrics); Ui.Gap(body, 10);
        var flowBody = new StackPanel();
        var directions = Ui.Columns(new(1, GridUnitType.Star), new(1, GridUnitType.Star));
        Ui.Put(directions, upstream, 0); downstream.HorizontalAlignment = HorizontalAlignment.Right; Ui.Put(directions, downstream, 1); flowBody.Children.Add(directions);
        Ui.Put(flow, new Border { Background = Ui.Blue }, 0); Ui.Put(flow, new Border { Background = Ui.Brush("#89BDBD") }, 1); Ui.Put(flow, new Border { Background = Ui.Accent }, 2);
        flowBody.Children.Add(new Border { Child = flow, Height = 5, ClipToBounds = true, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0) });
        flowBody.ToolTip = flowDetail; body.Children.Add(Ui.Card(flowBody, new Thickness(11))); Ui.Gap(body, 14);
        body.Children.Add(Section("产品用量", providerCount)); Ui.Gap(body, 9); body.Children.Add(providerRows); Ui.Gap(body, 8);
        var chartBody = new StackPanel(); chartBody.Children.Add(Section("近 7 天趋势", trendTotal)); Ui.Gap(chartBody, 12); chartBody.Children.Add(trend);
        body.Children.Add(Ui.Card(chartBody)); Ui.Gap(body, 10);
        localModelCard = Ui.Card(localModelRows, new Thickness(12)); localModelCard.Visibility = Visibility.Collapsed; body.Children.Add(localModelCard);
        var sourceHeader = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto); Ui.Put(sourceHeader, sourceTitle, 0); Ui.Put(sourceHeader, sourceChevron, 1);
        var sourceButton = new Button { Content = sourceHeader, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3, 10, 3, 10), Style = (Style)FindResource("SoftButton"), Background = Brushes.Transparent };
        sourceButton.Click += (_, _) => SetSourcesExpanded(!sourcesExpanded);
        body.Children.Add(sourceButton); sourceRows.Visibility = Visibility.Collapsed; body.Children.Add(sourceRows);
        root.Children.Add(new ScrollViewer { Name = "DashboardScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly });
        Content = Ui.Shell(root); Closing += OnClosing;
    }
    private static Grid Section(string title, TextBlock detail)
    {
        var grid = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto); Ui.Put(grid, Ui.Text(title, 12, Ui.Ink, FontWeights.SemiBold), 0); Ui.Put(grid, detail, 1); return grid;
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!AllowClose) { e.Cancel = true; Hide(); } }
    public void SetRefreshing(bool busy) { refreshButton.IsEnabled = !busy; refreshButton.ToolTip = busy ? "正在读取本地日志…" : "刷新本地日志"; }
    public void SetSourcesExpanded(bool expanded)
    {
        sourcesExpanded = expanded; sourceRows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; sourceChevron.Text = expanded ? "\uE70E" : "\uE70D";
    }
    public void ShowError(string? message) { errorText.Text = message ?? ""; errorBox.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
    public void Render(DashboardSnapshot snapshot, ScanResult scan, OllamaResult ollama)
    {
        var t = snapshot.Totals;
        tokens.Text = Formatting.Tokens(t.Total); cache.Text = t.CacheHitRatio.ToString("P1"); cost.Text = Formatting.Usd(t.Cost);
        sessions.Text = $"{t.Sessions} 会话 · {t.Events} 步"; sessions.ToolTip = sessions.Text;
        saved.Text = $"已省 {Formatting.Usd(t.Saved)}"; saved.ToolTip = $"缓存读取节省的估算费用：{Formatting.Usd(t.Saved)}";
        upstream.Text = $"↑ 上行 {Formatting.Tokens(t.Upstream)}"; downstream.Text = $"↓ 下行 {Formatting.Tokens(t.Output)}";
        flowDetail.Text = $"缓存读取 {Formatting.Tokens(t.CacheRead)}  ·  推理 {Formatting.Tokens(t.Reasoning)}";
        flowDetail.ToolTip = "上行包含输入、缓存写入与读取；推理 Token 已含于下行，不重复计数。";
        var widths = t.Total == 0 ? new double[] { 1, 0, 0 } : [(double)(t.Input + t.CacheWrite), t.CacheRead, t.Output];
        for (var i = 0; i < 3; i++) flow.ColumnDefinitions[i].Width = new GridLength(widths[i], GridUnitType.Star);
        alertBox.Visibility = snapshot.IsHighBurnRate ? Visibility.Visible : Visibility.Collapsed;
        alertText.Text = $"近 5 分钟 {Formatting.Tokens(snapshot.Recent.Total)} Token · {Formatting.Usd(snapshot.Recent.Cost)}";
        providerCount.Text = $"{snapshot.Providers.Count} 个来源"; providerRows.Children.Clear();
        if (snapshot.Providers.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(4, 6, 4, 6) }; empty.Children.Add(Ui.Text("等待第一条用量记录", 13, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(empty, 6);
            empty.Children.Add(Ui.Text("开始使用 AI 工具，或在设置中选择日志目录。", 11, Ui.Muted)); providerRows.Children.Add(Ui.Card(empty));
        }
        foreach (var provider in snapshot.Providers) providerRows.Children.Add(ProviderCard(provider));
        RenderTrend(snapshot.Trend); localModelRows.Children.Clear(); localModelCard.Visibility = ollama.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var model in ollama.Models) localModelRows.Children.Add(Ui.Text($"{model.Name}  ·  显存 {model.VramBytes / 1073741824.0:F1} GiB", 11, Ui.Muted));
        var ready = scan.Statuses.Count(s => s.State == SourceState.Ready && s.Message != "未发现日志"); var errors = scan.Statuses.Count(s => s.State == SourceState.Error);
        sourceTitle.Text = $"数据源  ·  {ready}/{scan.Statuses.Count} 已连接" + (errors > 0 ? $"  ·  {errors} 项需检查" : ""); sourceTitle.Foreground = errors > 0 ? Ui.Amber : Ui.Muted;
        sourceRows.Children.Clear();
        foreach (var source in scan.Statuses)
        {
            var row = new StackPanel { Margin = new Thickness(3, 0, 0, 12) }; row.Children.Add(Ui.Text($"{Providers.Name(source.Provider)} · {source.Message}", 10.5, source.State == SourceState.Error ? Ui.Amber : Ui.Muted));
            var path = Ui.Text(source.Path, 10, Ui.Muted); path.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(path); sourceRows.Children.Add(row);
        }
        sourceRows.Children.Add(Ui.Text(ollama.Status, 10.5, Ui.Muted)); updated.Text = $"更新于 {snapshot.GeneratedAt.LocalDateTime:HH:mm:ss}"; connection.Text = errors > 0 ? "部分数据源需检查" : "本地只读";
    }
    private static Border ProviderCard(ProviderSummary provider)
    {
        var panel = new StackPanel(); var header = Ui.Columns(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        var (shortName, foreground, background) = provider.Provider switch
        {
            Providers.Codex => ("Cx", Ui.Accent, "#EEEBFB"), Providers.Claude => ("Cl", Ui.Brush("#B47C5B"), "#FBF0E9"),
            Providers.Antigravity => ("G", Ui.Blue, "#EAF3FB"), "ollama" => ("Ol", Ui.Ink, "#ECEEF3"), _ => ("•", Ui.Green, "#EAF6EF")
        };
        Ui.Put(header, new Border { Child = Ui.Text(shortName, 12, foreground, FontWeights.SemiBold), Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Ui.Brush(background), Padding = new Thickness(7, 0, 0, 0), Margin = new Thickness(0, 0, 9, 0) }, 0);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; identity.Children.Add(Ui.Text(Providers.Name(provider.Provider), 12, Ui.Ink, FontWeights.SemiBold));
        var model = Ui.Text(string.Join(" · ", provider.TopModels), 10, Ui.Muted); model.Margin = new Thickness(0, 3, 5, 0); model.TextWrapping = TextWrapping.NoWrap; model.TextTrimming = TextTrimming.CharacterEllipsis; model.ToolTip = model.Text;
        identity.Children.Add(model); Ui.Put(header, identity, 1);
        var values = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right }; values.Children.Add(Ui.Text(Formatting.Tokens(provider.Totals.Total), 16, Ui.Ink, FontWeights.SemiBold));
        var price = Ui.Text(Formatting.Usd(provider.Totals.Cost), 10, Ui.Muted); price.HorizontalAlignment = HorizontalAlignment.Right; price.Margin = new Thickness(0, 2, 0, 0); values.Children.Add(price); Ui.Put(header, values, 2); panel.Children.Add(header);
        Ui.Gap(panel, 8); panel.Children.Add(Ui.Text($"↑ {Formatting.Tokens(provider.Totals.Upstream)}     ↓ {Formatting.Tokens(provider.Totals.Output)}     缓存 {provider.Totals.CacheHitRatio:P1}", 10.5, Ui.Muted));
        if (provider.RateLimit is { } quota)
        {
            AddQuota(panel, quota.Primary); if (quota.Secondary is { } secondary) AddQuota(panel, secondary);
            panel.ToolTip = $"配额记录于 {quota.UpdatedAt.LocalDateTime:g}" + (quota.PlanType is { } plan ? $" · {plan}" : "") + "；以日志为准";
        }
        var card = Ui.Card(panel, new Thickness(12)); card.Margin = new Thickness(0, 0, 0, 8); return card;
    }
    private static void AddQuota(Panel panel, QuotaWindow quota)
    {
        Ui.Gap(panel, 8);
        var duration = quota.WindowMinutes is { } minutes ? minutes % 1440 == 0 ? $"{minutes / 1440} 天" : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟" : "窗口";
        var label = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto); Ui.Put(label, Ui.Text(duration + "配额", 10, Ui.Muted), 0);
        var color = quota.UsedPercent >= 90 ? Ui.Amber : Ui.Accent; Ui.Put(label, Ui.Text($"已用 {quota.UsedPercent:F1}%", 10, color), 1); panel.Children.Add(label);
        var pct = Math.Clamp(quota.UsedPercent, 0, 100); var bar = Ui.Columns(new(pct, GridUnitType.Star), new(100 - pct, GridUnitType.Star)); Ui.Put(bar, new Border { Background = color, CornerRadius = new CornerRadius(2) }, 0);
        var track = new Border { Child = bar, Height = 4, Background = Ui.Brush("#EEEDF7"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 0, 0) }; track.ToolTip = quota.ResetsAt is { } reset ? $"重置于 {reset.LocalDateTime:g}" : "日志未提供重置时间"; panel.Children.Add(track);
    }
    private void RenderTrend(IReadOnlyList<DailyPoint> points)
    {
        trend.Children.Clear(); trendTotal.Text = Formatting.Tokens(points.Sum(p => p.Total)) + " Token"; var max = Math.Max(1, points.Select(p => p.Total).DefaultIfEmpty(0).Max());
        for (var index = 0; index < points.Count; index++)
        {
            var day = points[index]; var today = index == points.Count - 1; var column = new Grid { Margin = new Thickness(3, 0, 3, 0) };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) }); column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });
            var value = Ui.Text(day.Total == 0 ? "—" : Formatting.Tokens(day.Total), 9, today ? Ui.Accent : Ui.Muted); value.HorizontalAlignment = HorizontalAlignment.Center; column.Children.Add(value);
            var height = Math.Max(3, 41.0 * day.Total / max);
            var bar = new Border { Height = height, Width = 21, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4, 4, 2, 2), Background = day.Total == 0 ? Ui.Brush("#EBEDF4") : new LinearGradientBrush(today ? Color.FromRgb(154, 135, 242) : Color.FromRgb(196, 184, 243), today ? Color.FromRgb(113, 98, 223) : Color.FromRgb(159, 142, 225), 90) };
            Grid.SetRow(bar, 1); column.Children.Add(bar); var date = Ui.Text(day.Date.ToString("MM/dd"), 9, today ? Ui.Accent : Ui.Muted, today ? FontWeights.SemiBold : FontWeights.Normal); date.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetRow(date, 2); column.Children.Add(date);
            column.ToolTip = $"{day.Date:MM/dd} · ↑ {day.Upstream:N0} · ↓ {day.Output:N0} · {Formatting.Usd(day.Cost)}"; trend.Children.Add(column);
        }
    }
    public void OpenNearTray()
    {
        if (IsVisible) { Activate(); return; } Show(); var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea; var handle = new WindowInteropHelper(this).Handle;
        NativePosition.SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, 0, 0, 0x0015);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity; var available = transform.Transform(new Vector(area.Width - 24, area.Height - 24));
        MaxHeight = Math.Max(MinHeight, available.Y); MaxWidth = Math.Max(MinWidth, available.X); UpdateLayout();
        if (NativePosition.GetWindowRect(handle, out var bounds))
        {
            var width = Math.Min(bounds.Right - bounds.Left, area.Width - 24); var height = Math.Min(bounds.Bottom - bounds.Top, area.Height - 24);
            NativePosition.SetWindowPos(handle, IntPtr.Zero, area.Right - width - 12, area.Bottom - height - 12, width, height, 0x0014);
        }
        Activate();
    }
    private static class NativePosition
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")][return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [System.Runtime.InteropServices.DllImport("user32.dll")][return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
