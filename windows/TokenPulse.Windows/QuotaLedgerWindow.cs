using System.Globalization;
using System.Windows.Automation;

namespace TokenPulse.Windows;

/// <summary>An independent native window over frozen, numeric-only ledger reports.</summary>
internal sealed class QuotaLedgerWindow : Window
{
    private readonly QuotaLedgerSession session;
    private readonly TextBlock status = Ui.Text("", 11, Ui.Muted);
    private readonly TextBlock operation = Ui.Text("", 10.5, Ui.Muted);
    private readonly StackPanel reportBody = new();
    private readonly StackPanel historyPanel = new();
    private readonly StackPanel customPanel = new();
    private readonly ComboBox history = new() { MinHeight = 34, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox customStart = new() { MinWidth = 140 };
    private readonly TextBox customEnd = new() { MinWidth = 140 };
    private readonly Button export;
    private readonly Button clear;
    private readonly Button rebuild;
    private readonly Button generate;
    private string mode = "current";
    private bool busy, changingHistory;
    private QuotaReport? customReport, displayed;
    private string? selectedCycle;
    private string historySignature = "";
    private QuotaLedgerView? lastView;

    public QuotaLedgerWindow(QuotaLedgerSession session)
    {
        this.session = session;
        Title = $"TokenPulse 配额账本 · {Ui.Version}";
        Width = 880; Height = 820; MinWidth = 520; MinHeight = 540;
        ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.ConfigureWindow(this);
        var root = new DockPanel { Name = "QuotaLedgerContent", Margin = new Thickness(22, 18, 22, 16) };
        var top = new StackPanel();
        top.Children.Add(Ui.Header(this, "配额账本", "原生 Codex · 冻结时区 · 最近 90 天摘要"));
        var tabs = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3 };
        foreach (var (label, value) in new[] { ("当前周期", "current"), ("历史账本", "history"), ("自定义", "custom") })
        {
            var tab = new RadioButton { Content = label, GroupName = "QuotaLedgerMode", IsChecked = value == mode,
                Style = (Style)FindResource("Segment") };
            tab.Checked += (_, _) => { mode = value; Refresh(); };
            tabs.Children.Add(tab);
        }
        top.Children.Add(new Border { Child = tabs, CornerRadius = new CornerRadius(9), Background = Ui.Brush("#EEEFF6"),
            Padding = new Thickness(2), Margin = new Thickness(0, 16, 0, 12) });
        status.Margin = new Thickness(2, 0, 2, 12); top.Children.Add(status);
        historyPanel.Children.Add(Ui.Text("选择账本周期", 10.5, Ui.Muted)); Ui.Gap(historyPanel, 6);
        history.DisplayMemberPath = nameof(CycleChoice.Label);
        AutomationProperties.SetName(history, "选择账本周期");
        history.SelectionChanged += (_, _) =>
        {
            if (changingHistory) return;
            selectedCycle = (history.SelectedItem as CycleChoice)?.Key;
            Refresh();
        };
        historyPanel.Children.Add(history); historyPanel.Margin = new Thickness(0, 0, 0, 12); top.Children.Add(historyPanel);
        var dates = Ui.Columns(new(1, GridUnitType.Star), new(12), new(1, GridUnitType.Star));
        DateInput(dates, "开始本地时间", customStart, 0);
        DateInput(dates, "结束本地时间 不包含", customEnd, 2);
        customPanel.Children.Add(dates); Ui.Gap(customPanel, 7);
        customPanel.Children.Add(Ui.Text("格式 yyyy-MM-dd HH:mm；本机时区 " + TimeZoneInfo.Local.Id + "；统计最多截至最新快照。", 10, Ui.Muted));
        generate = Ui.Button("生成自定义快照", () => _ = GenerateCustomAsync()); generate.HorizontalAlignment = HorizontalAlignment.Left;
        generate.Margin = new Thickness(0, 8, 0, 0); customPanel.Children.Add(generate);
        customPanel.Margin = new Thickness(0, 0, 0, 12); top.Children.Add(customPanel);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        clear = Ui.Button("清空已结束历史", () => _ = ClearAsync());
        rebuild = Ui.Button("从现存日志重建", () => _ = RebuildAsync());
        export = Ui.Button("导出 HTML 与 CSV", () => _ = ExportAsync(), true);
        foreach (var button in new[] { clear, rebuild, export }) { button.Margin = new Thickness(0, 4, 7, 0); buttons.Children.Add(button); }
        bottom.Children.Add(buttons); operation.Margin = new Thickness(2, 8, 2, 0); bottom.Children.Add(operation);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Name = "QuotaLedgerScroll", Content = reportBody,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = Ui.Shell(root, 1040, 980);
        var now = DateTime.Now;
        customStart.Text = now.AddDays(-7).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        customEnd.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Refresh();
    }

    private static void DateInput(Grid row, string label, TextBox input, int column)
    {
        var panel = new StackPanel(); panel.Children.Add(Ui.Text(label, 10.5, Ui.Muted)); Ui.Gap(panel, 5);
        AutomationProperties.SetName(input, label); panel.Children.Add(input); Ui.Put(row, panel, column);
    }

    public void Refresh()
    {
        var view = session.View(); lastView = view;
        status.Text = view.Status;
        status.Foreground = view.StoreCorrupt || !view.NativeSourceAvailable ? Ui.Amber : Ui.Muted;
        var cycles = view.Cycles.OrderByDescending(c => c.Report.EndUtc).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
        var signature = string.Join("|", cycles.Select(c => c.Key + ":" + c.Archived));
        if (signature != historySignature || history.Items.Count != cycles.Length)
        {
            changingHistory = true;
            try
            {
                history.ItemsSource = cycles.Select(c => new CycleChoice(c.Key,
                    $"{ShortBoundary(c.Report.StartLocal, c.Report.StartUtc)} → {ShortBoundary(c.Report.EndLocal, c.Report.EndUtc)} · {(c.Archived ? "已归档" : "活动周期")}")).ToArray();
                history.SelectedIndex = Array.FindIndex(cycles, c => c.Key == selectedCycle);
                if (history.SelectedIndex < 0 && cycles.Length > 0) history.SelectedIndex = 0;
                selectedCycle = (history.SelectedItem as CycleChoice)?.Key;
                historySignature = signature;
            }
            finally { changingHistory = false; }
        }
        historyPanel.Visibility = mode == "history" ? Visibility.Visible : Visibility.Collapsed;
        customPanel.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (!view.NativeSourceAvailable) customReport = null;
        var chosen = cycles.FirstOrDefault(c => c.Key == selectedCycle);
        displayed = mode switch { "history" => chosen?.Report, "custom" => customReport, _ => view.Current };
        reportBody.Children.Clear();
        if (displayed is { } report) RenderReport(report, mode == "history" ? chosen?.Archived : null);
        else RenderEmpty(view);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var view = lastView;
        export.IsEnabled = !busy && displayed is not null && (mode == "history" || view?.NativeSourceAvailable == true);
        clear.IsEnabled = !busy && view is { StoreCorrupt: false } && view.Cycles.Any(c => c.Archived);
        clear.ToolTip = view?.StoreCorrupt == true ? "损坏账本不能普通清空，请确认后重建。" : "仅清空已结束周期；普通刷新不回填，显式重建可恢复现存日志中的历史。";
        rebuild.IsEnabled = !busy && view?.NativeSourceAvailable == true;
        rebuild.ToolTip = "以本次已扫描的原生 Codex 数据重建；损坏原文件保留备份，已删除的日志无法恢复。";
        generate.IsEnabled = !busy && view?.NativeSourceAvailable == true;
        history.IsEnabled = !busy && history.Items.Count > 0;
    }

    private void RenderEmpty(QuotaLedgerView view)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(mode == "history" ? "还没有可用历史数字摘要" : mode == "custom"
            ? "选择本地时间范围后生成冻结快照" : "当前周配额尚无可靠记录", 17, Ui.Ink, FontWeights.SemiBold));
        Ui.Gap(panel, 8);
        panel.Children.Add(Ui.Text(!view.NativeSourceAvailable
            ? "当前原生 Codex 来源不可用。已保留的历史可在「历史账本」中查看和导出。"
            : mode == "custom" ? "只统计已扫描的原生 Codex 数字用量，自定义 JSONL 中的 Codex 记录不参与。"
            : "只有时间、窗口长度和重置时刻均有效的周配额记录才能确认周期；不会使用 0% 代替未知额度。", 11, Ui.Muted));
        if (view.ClearedEndedBeforeUtc is { } cleared)
        {
            Ui.Gap(panel, 10); panel.Children.Add(Ui.Text("历史清空屏障 " + Date(cleared) + " UTC；普通刷新不会回填此前已结束周期。", 10.5, Ui.Muted));
        }
        reportBody.Children.Add(Ui.Card(panel, new Thickness(18)));
    }

    private void RenderReport(QuotaReport report, bool? archived)
    {
        var intro = new StackPanel();
        var label = mode == "custom" ? "自定义冻结快照" : archived == true ? "历史归档快照" : "本轮周配额快照";
        intro.Children.Add(Ui.Text(label, 16, Ui.Ink, FontWeights.SemiBold));
        Ui.Gap(intro, 5); intro.Children.Add(Ui.Text("快照 " + Date(report.GeneratedAtUtc) + " UTC", 10.5, Ui.Muted));
        Ui.Gap(intro, 10);
        var numbers = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 1 };
        NumberCard(numbers, "日志配额观察", report.Quota is { } q ? q.UsedPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "未绑定", Ui.Accent);
        NumberCard(numbers, "原生 Token 合计", Formatting.Tokens(report.Totals.Total), Ui.Blue);
        NumberCard(numbers, "等效 API 金额", Formatting.Usd(report.Totals.EstimatedUSD), Ui.Amber);
        intro.Children.Add(numbers); Ui.Gap(intro, 10);
        intro.Children.Add(Ui.Text($"{Boundary(report.StartLocal, report.StartUtc)} 至 {Boundary(report.EndLocal, report.EndUtc)}", 11, Ui.Ink));
        intro.Children.Add(Ui.Text("冻结时区 " + report.TimeZoneId + " · 统计截至 " + Boundary(report.StatisticsEndLocal, report.StatisticsEndUtc) + "，不包含截止时刻。", 10.5, Ui.Muted));
        if (report.Quota is { } quota)
        {
            Ui.Gap(intro, 6);
            intro.Children.Add(Ui.Text($"配额记录 {Date(quota.RecordedAtUtc)} UTC · {quota.WindowMinutes} 分钟 · {quota.WindowRole}", 10.5, Ui.Muted));
            intro.Children.Add(Ui.Text("下次重置 " + Date(quota.ResetsAtUtc) + " UTC · 套餐 " + (string.IsNullOrWhiteSpace(quota.PlanType) ? "未知" : quota.PlanType), 10.5, Ui.Muted));
        }
        if (report.FirstObserved100AtUtc is { } exhausted)
            intro.Children.Add(Ui.Text("首次观测 100% " + Date(exhausted) + " UTC；之后真实用量仍计入。", 10.5, Ui.Amber));
        Ui.Gap(intro, 8); intro.Children.Add(Ui.Text(report.Coverage, 10.5, Ui.Muted));
        reportBody.Children.Add(Ui.Card(intro)); Ui.Gap(reportBody, 14);
        reportBody.Children.Add(Ui.Text("Token 构成", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(reportBody, 7);
        var totals = report.Totals;
        reportBody.Children.Add(Ui.Card(DetailTable(new[] { "分类", "Token 数量", "说明" }, new[]
        {
            new[] { "普通输入", Count(totals.Input), "不含缓存" }, new[] { "缓存写入", Count(totals.CacheWrite), "已计入上行" },
            new[] { "缓存读取", Count(totals.CacheRead), totals.CacheHitRatio.ToString("P2", CultureInfo.InvariantCulture) + " 命中" },
            new[] { "输出", Count(totals.Output), "含推理 " + Count(totals.Reasoning) }
        }), new Thickness(12)));
        Ui.Gap(reportBody, 14); reportBody.Children.Add(Ui.Text("模型明细", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(reportBody, 7);
        reportBody.Children.Add(Ui.Card(DetailTable(new[] { "模型", "Token", "USD 估算" }, report.Models.Select(m =>
            new[] { m.Model, Count(m.Totals.Total), Formatting.Usd(m.Totals.EstimatedUSD) }).ToArray()), new Thickness(12)));
        Ui.Gap(reportBody, 14); reportBody.Children.Add(Ui.Text("每日明细 · " + report.TimeZoneId, 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(reportBody, 7);
        reportBody.Children.Add(Ui.Card(DetailTable(new[] { "本地日期", "Token", "USD 估算" }, report.Days.Select(d =>
            new[] { d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Count(d.Totals.Total), Formatting.Usd(d.Totals.EstimatedUSD) }).ToArray()), new Thickness(12)));
        Ui.Gap(reportBody, 12);
        reportBody.Children.Add(Ui.Text($"{report.PricingVersion} · {report.PricingBasis}。{totals.Sessions} 个会话、{totals.Events} 条记录。模型与每日会话数不能相加当作总会话数。", 10, Ui.Muted));
        Ui.Gap(reportBody, 7);
        reportBody.Children.Add(Ui.Text("全部边界按 UTC [起点, 截止) 统计。周起点由重置时刻减窗口长度推定；配额百分比与本机金额仅按时间对齐，不代表固定套餐美元额度。已删除日志、其他设备或未记录用量可能不在本机汇总中。", 10, Ui.Muted));
    }

    private static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Boundary(string frozen, DateTimeOffset utc) => string.IsNullOrWhiteSpace(frozen) ? Date(utc) + " UTC" : frozen;
    private static string ShortBoundary(string frozen, DateTimeOffset utc)
    {
        var text = Boundary(frozen, utc).Replace('T', ' ');
        return text.Length > 16 ? text[..16] : text;
    }
    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static void NumberCard(Panel parent, string label, string value, Brush color)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 9, 0) };
        panel.Children.Add(Ui.Text(label, 10, Ui.Muted));
        panel.Children.Add(new Viewbox { Child = Ui.Text(value, 23, color, FontWeights.SemiBold), Height = 34,
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        parent.Children.Add(panel);
    }

    private static Grid DetailTable(string[] headings, string[][] rows)
    {
        var table = Ui.Columns(new(1.65, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star));
        var all = new[] { headings }.Concat(rows).ToArray();
        for (var row = 0; row < all.Length; row++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < headings.Length; column++)
            {
                var text = Ui.Text(all[row][column], row == 0 ? 10 : 10.5, row == 0 ? Ui.Muted : Ui.Ink,
                    row == 0 ? FontWeights.SemiBold : FontWeights.Normal);
                text.Margin = new Thickness(column == 0 ? 0 : 10, 5, 0, 5);
                if (column > 0) text.TextAlignment = TextAlignment.Right;
                Grid.SetRow(text, row); Grid.SetColumn(text, column); table.Children.Add(text);
            }
        }
        if (rows.Length == 0)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = Ui.Text("本范围没有可确认时间的记录。", 10.5, Ui.Muted); empty.Margin = new Thickness(0, 8, 0, 4);
            Grid.SetRow(empty, 1); Grid.SetColumnSpan(empty, headings.Length); table.Children.Add(empty);
        }
        return table;
    }

    private async Task GenerateCustomAsync()
    {
        if (busy) return;
        if (!DateTime.TryParseExact(customStart.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startLocal)
            || !DateTime.TryParseExact(customEnd.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endLocal))
        { SetOperation("请使用本地时间格式 yyyy-MM-dd HH:mm，例如 2026-10-21 15:00。", true); return; }
        startLocal = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
        endLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(startLocal) || zone.IsInvalidTime(endLocal) || zone.IsAmbiguousTime(startLocal) || zone.IsAmbiguousTime(endLocal))
        { SetOperation("所选时刻处于夏令时跳变或重复区间，请选择没有歧义的本地时刻。", true); return; }
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone));
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone));
        busy = true; UpdateButtons();
        try
        {
            customReport = await Task.Run(() => session.Custom(start, end));
            SetOperation("自定义快照已生成，切换标签或自动刷新不会重新读取原始日志。", false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { SetOperation(ex.Message, true); }
        finally { busy = false; Refresh(); }
    }

    private async Task ClearAsync()
    {
        if (busy || !clear.IsEnabled) return;
        if (MessageBox.Show(this, "只清空已结束周期的数字摘要，当前周期和原始日志保持不变。\n普通刷新不会回填已清除历史；手动重建可恢复现存日志中的历史。\n\n确认清空？", "清空已结束历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WorkAsync(session.ClearEndedHistory, "已清空已结束历史；普通刷新不会回填。");
    }

    private async Task RebuildAsync()
    {
        if (busy || !rebuild.IsEnabled) return;
        if (MessageBox.Show(this, "将根据本次已扫描的原生 Codex 数据重建账本，并解除历史清空屏障。\n损坏原文件会保留备份；已删除的日志无法恢复。\n\n确认重建？", "从现存日志重建", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WorkAsync(session.Rebuild, "账本已从已扫描数据重建，原始日志没有修改。");
    }

    private async Task WorkAsync(Action work, string success)
    {
        busy = true; UpdateButtons();
        try { await Task.Run(work); SetOperation(success, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { SetOperation(ex.Message, true); }
        finally { busy = false; Refresh(); }
    }

    private async Task ExportAsync()
    {
        if (busy || displayed is not { } frozen) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择报告父目录，将创建新的 HTML 与 CSV 子目录" };
        if (dialog.ShowDialog(this) != true) return;
        busy = true; UpdateButtons();
        try
        {
            var parent = dialog.FolderName;
            var directory = await Task.Run(() => QuotaReportExport.Export(frozen, parent));
            SetOperation("已导出冻结快照到 " + directory, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { SetOperation("导出失败，未替换已有报告：" + ex.Message, true); }
        finally { busy = false; UpdateButtons(); }
    }

    private void SetOperation(string text, bool error) { operation.Text = text; operation.Foreground = error ? Ui.Amber : Ui.Green; }
    private sealed record CycleChoice(string Key, string Label);
}
