using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TokenPulse.Windows;

/// <summary>Render the actual WPF windows with synthetic data on Windows CI; never reads user logs.</summary>
internal static class UiSnapshots
{
    public static async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        // Keep all synthetic "today" records on one local date, even when CI runs
        // around midnight. Historical/expiry scenarios must not depend on wall time.
        var now = new DateTimeOffset(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Local));
        var today = new UsageEvent("preview", Providers.Codex, "gpt-6.1-sol", now.AddMinutes(-1), "preview", 91000, 0, 1154000, 6600, 764, .81, 1.73);
        var events = new List<UsageEvent> { today };
        long[] history = [3250000, 2460000, 4230000, 0, 8760000, 3120000];
        for (var i = 0; i < history.Length; i++)
            events.Add(UsageEvent.Create("history-" + i, Providers.Codex, "gpt-6.1-sol", now.AddDays(i - 6), "history-" + i, history[i], 0, 0, 0));
        var quota = new RateLimit(Providers.Codex, null, new(29, 10080, now.AddDays(2)), null, now);
        SourceStatus[] sources = [new(Providers.Codex, @"C:\Users\Example\.codex\sessions", SourceState.Ready, "已读取 24 个日志文件"),
            new(Providers.Claude, @"C:\Users\Example\.claude\projects", SourceState.Missing, "未发现日志"),
            new(Providers.Antigravity, @"C:\Users\Example\.gemini\antigravity\brain", SourceState.Missing, "未发现日志"),
            new(Providers.Custom, @"C:\Users\Example\.tokenpulse\custom_events.jsonl", SourceState.Missing, "未发现日志")];
        var scan = new ScanResult(events, [quota], sources);
        var models = new OllamaResult([], "Ollama 未连接");
        var changed = 0;
        var settingsOpened = 0;
        var window = new MainWindow(() => { }, () => settingsOpened++, () => changed++);
        try
        {
            window.Render(Aggregation.Build(scan, TimeWindow.Today, now), scan, models);
            await ShowAndCapture(window, directory, "dashboard-warning");
            foreach (var size in new[] { new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
                CaptureViewport(window, directory, $"dashboard-wide-{(int)size.Width}", size);
            window.OpenNearTray();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var restoreBounds = window.RestoreBounds;
            Ui.ToggleMaximize(window);
            await ShowAndCapture(window, directory, "dashboard-maximized");
            if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("Maximize did not enter the maximized state.");
            window.Close();
            window.OpenNearTray();
            if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("Reopening a maximized dashboard lost its window state.");
            Ui.ToggleMaximize(window);
            await ShowAndCapture(window, directory, "dashboard-restored");
            if (window.WindowState != WindowState.Normal || Math.Abs(window.RestoreBounds.Width - restoreBounds.Width) > 2
                || Math.Abs(window.RestoreBounds.Height - restoreBounds.Height) > 2)
                throw new InvalidOperationException("Restoring the dashboard did not preserve its normal bounds.");
            var radio = Descendants<RadioButton>(window).Single(r => Equals(r.Content, "近 7 天"));
            changed = 0;
            radio.IsChecked = true;
            if (window.SelectedWindow != TimeWindow.SevenDays || changed != 1) throw new InvalidOperationException("Time-window segment did not select correctly.");
            Descendants<RadioButton>(window).Single(r => Equals(r.Content, "今日")).IsChecked = true;
            window.Close();
            if (window.IsVisible) throw new InvalidOperationException("Closing the dashboard must hide it.");
            var calm = scan with { Events = events.Select(e => e == today ? e with { Timestamp = now.AddHours(-1) } : e).ToArray() };
            window.Render(Aggregation.Build(calm, TimeWindow.Today, now), calm, models);
            await ShowAndCapture(window, directory, "dashboard-normal");
            var empty = new ScanResult([], [], sources.Select(s => s with { State = SourceState.Missing, Message = "未发现日志" }).ToArray());
            window.Render(Aggregation.Build(empty, TimeWindow.Today, now), empty, models);
            await ShowAndCapture(window, directory, "dashboard-empty");
            var large = new ScanResult([today with { InputTokens = 91_000_000, CacheReadTokens = 1_154_000_000, CostUSD = 1_234_567.8 }], [], sources);
            window.Width = 420;
            window.Render(Aggregation.Build(large, TimeWindow.Today, now), large, models);
            await ShowAndCapture(window, directory, "dashboard-large-values");
            var more = new[]
            {
                today,
                UsageEvent.Create("codex-second", Providers.Codex, "gpt-5-codex", now.AddHours(-1), "preview", 12500, 1000, 32000, 800),
                UsageEvent.Create("claude", Providers.Claude, "claude-sonnet-4-5-long-model-name", now.AddHours(-1), "claude", 12500, 6000, 160000, 8500),
                UsageEvent.Create("gemini", Providers.Antigravity, "gemini-antigravity", now.AddHours(-1), "gemini", 40000, 0, 80000, 3000)
            };
            var mixed = new ScanResult(more, [quota with { Primary = new(95.2, 300, now.AddHours(2)), Secondary = new(29, 10080, now.AddDays(2)) }],
                sources.Select(s => s.Provider == Providers.Claude ? s with { State = SourceState.Error, Path = @"C:\Users\示例用户\开发工作区\自定义会话与日志\Claude Code\projects", Message = "访问失败：需要检查目录权限" } : s).ToArray());
            window.Width = 420; window.Render(Aggregation.Build(mixed, TimeWindow.Today, now), mixed, new([new("qwen-local", 8_000_000_000, 6_000_000_000)], "Ollama 已连接"));
            await ShowAndCapture(window, directory, "dashboard-narrow-multiple");
            var modelToggle = Descendants<Button>(window).Single(b => Equals(b.Content, "查看 2 个模型  +"));
            modelToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            // The four-second refresh must not collapse details the user is reading.
            window.Render(Aggregation.Build(mixed, TimeWindow.Today, now), mixed, models);
            if (!Descendants<Button>(window).Contains(modelToggle))
                throw new InvalidOperationException("Unchanged refresh rebuilt interactive model rows.");
            await ShowAndCapture(window, directory, "dashboard-model-details");
            if (Descendants<StackPanel>(window).Count(p => p.Name == "ModelDetails" && p.Visibility == Visibility.Visible) != 1)
                throw new InvalidOperationException("Model expansion did not survive refresh.");
            var expired = mixed with { RateLimits = [quota with { Primary = new(95, 300, now.AddSeconds(-1)), Secondary = new(29, 10080, now.AddDays(2)) }] };
            window.Render(Aggregation.Build(expired, TimeWindow.Today, now), expired, models);
            await ShowAndCapture(window, directory, "dashboard-expired-quota");
            if (!Descendants<TextBlock>(window).Any(t => t.Text == "已到重置时间 · 待更新")
                || Descendants<TextBlock>(window).Any(t => t.Text == "已用 95.0%"))
                throw new InvalidOperationException("Expired quota is presented as current.");
            // Expiry must update even when the underlying log has not changed.
            window.Render(Aggregation.Build(mixed, TimeWindow.Today, now), mixed, models);
            window.Render(Aggregation.Build(mixed, TimeWindow.Today, now.AddHours(3)), mixed, models);
            if (!Descendants<TextBlock>(window).Any(t => t.Text == "已到重置时间 · 待更新"))
                throw new InvalidOperationException("Unchanged quota did not expire as time advanced.");
            var export = Descendants<Button>(window).Single(b => Equals(b.ToolTip, "导出当前周期的模型用量 CSV"));
            if (!export.IsEnabled) throw new InvalidOperationException("Rendered summary cannot be exported.");
            radio.IsChecked = true;
            if (export.IsEnabled) throw new InvalidOperationException("Export must wait for the new period's summary.");
            Descendants<RadioButton>(window).Single(r => Equals(r.Content, "今日")).IsChecked = true;
            window.Render(Aggregation.Build(mixed, TimeWindow.Today, now), mixed, models);
            window.SetSourcesExpanded(true);
            Descendants<ScrollViewer>(window).Single(s => s.Name == "DashboardScroll").ScrollToEnd();
            await ShowAndCapture(window, directory, "dashboard-source-details");
            window.SetSourcesExpanded(false);
            Descendants<ScrollViewer>(window).Single(s => s.Name == "DashboardScroll").ScrollToTop();
            var disabled = new ScanResult([], [], sources.Select(s => s with { State = SourceState.Disabled, Message = "已关闭 · 不扫描" }).ToArray());
            window.Render(Aggregation.Build(disabled, TimeWindow.Today, now), disabled, new([], "Ollama 查询已关闭"));
            await ShowAndCapture(window, directory, "dashboard-sources-disabled");
            if (!Descendants<TextBlock>(window).Any(t => t.Text == "日志扫描已关闭"))
                throw new InvalidOperationException("Disabled sources were presented as missing logs.");
            Descendants<Button>(window).Single(b => Equals(b.Content, "设置数据源"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (settingsOpened != 1) throw new InvalidOperationException("Empty-state settings action was not wired.");
            window.Render(Aggregation.Build(scan, TimeWindow.Today, now) with { AlertThresholds = new(0, 0) }, scan, models);
            if (Descendants<Border>(window).Single(b => b.Name == "HighBurnAlert").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Disabled burn warning remains visible.");
            await ShowAndCapture(window, directory, "dashboard-alerts-disabled");
            window.SetDataPending();
            if (export.IsEnabled) throw new InvalidOperationException("Settings change left stale export enabled.");
            var uncertain = new ScanResult([today with { HasKnownTimestamp = false }], [],
                [new(Providers.Codex, sources[0].Path, SourceState.Warning,
                    "已读取 1 个文件 · 1 条用量记录 · 2 行 JSON 损坏，已跳过 · 1 个末行疑似未写完，等待追加 · 1 条时间未知，仅计入全部用量")]);
            window.SetSourcesExpanded(true);
            window.Render(Aggregation.Build(uncertain, TimeWindow.Today, now), uncertain, models);
            if (Descendants<Border>(window).Single(b => b.Name == "HighBurnAlert").Visibility != Visibility.Collapsed
                || !Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("1 条记录时间未知") && t.Visibility == Visibility.Visible && t.TextWrapping == TextWrapping.Wrap)
                || !Descendants<TextBlock>(window).Any(t => t.Text == "当前周期没有可确认时间的记录")
                || !Descendants<TextBlock>(window).Any(t => t.Text.Contains("1 项需检查")))
                throw new InvalidOperationException("Uncertain usage or parser warnings are not displayed correctly.");
            await ShowAndCapture(window, directory, "dashboard-data-quality");
            window.Render(Aggregation.Build(empty, TimeWindow.Today, now), empty, models);
            if (!Descendants<TextBlock>(window).Any(t => t.Text == "等待第一条用量记录")
                || Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("0 条记录时间未知") && t.Visibility == Visibility.Visible))
                throw new InvalidOperationException("Data-quality notice or empty state did not clear on refresh.");
        }
        finally { window.AllowClose = true; window.Close(); }
        Settings? saved = null;
        var settings = new SettingsWindow(new Settings { StartWithWindows = true, ClaudeDirectory = sources[1].Path,
            CodexDirectory = sources[0].Path, AntigravityDirectory = sources[2].Path, CustomEventsFile = sources[3].Path },
            candidate => { saved = candidate; return "测试模式：未写入设置"; });
        try
        {
            await ShowAndCapture(settings, directory, "settings");
            CaptureViewport(settings, directory, "settings-wide-1920", new Size(1920, 1080));
            Ui.ToggleMaximize(settings);
            await ShowAndCapture(settings, directory, "settings-maximized");
            if (settings.WindowState != WindowState.Maximized) throw new InvalidOperationException("Settings did not maximize.");
            Ui.ToggleMaximize(settings);
            var sourceToggle = Descendants<CheckBox>(settings).Single(c => Equals(c.ToolTip, "启用 Claude Code"));
            var sourcePath = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Claude Code");
            sourceToggle.IsChecked = false;
            if (sourcePath.IsEnabled) throw new InvalidOperationException("Disabled source path remains editable.");
            var originalPath = sourcePath.Text;
            settings.Width = 460;
            await ShowAndCapture(settings, directory, "settings-source-disabled");
            Descendants<RadioButton>(settings).Single(r => Equals(r.Content, "运行偏好")).IsChecked = true;
            await ShowAndCapture(settings, directory, "settings-preferences");
            if (Descendants<CheckBox>(settings).Count() != 6) throw new InvalidOperationException("Expected four source and two preference switches.");
            var tokenLimit = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Token 阈值（整数）");
            var costLimit = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "等效费用阈值（USD）");
            var save = Descendants<Button>(settings).Single(b => Equals(b.Content, "保存设置"));
            tokenLimit.Text = "-1";
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (saved is not null) throw new InvalidOperationException("Negative alert threshold was accepted.");
            tokenLimit.Text = "500000"; costLimit.Text = "0";
            Descendants<RadioButton>(settings).Single(r => Equals(r.Content, "数据源")).IsChecked = true;
            if (sourcePath.Text != originalPath || sourceToggle.IsChecked != false || tokenLimit.Text != "500000")
                throw new InvalidOperationException("Settings tab switch lost unsaved edits.");
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (saved is not { EnableClaude: false, BurnRateTokens: 500000, BurnRateUSD: 0 } || saved.ClaudeDirectory != originalPath)
                throw new InvalidOperationException("Source/threshold changes did not reach the save callback.");
            Descendants<RadioButton>(settings).Single(r => Equals(r.Content, "运行偏好")).IsChecked = true;
            Descendants<Button>(settings).Single(b => Equals(b.Content, "恢复默认阈值"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (saved is not { BurnRateTokens: 250000, BurnRateUSD: 1.5 })
                throw new InvalidOperationException("Restoring default thresholds did not update the settings.");
            sourceToggle.IsChecked = true; sourcePath.Text = "relative";
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (Descendants<RadioButton>(settings).Single(r => Equals(r.Content, "数据源")).IsChecked != true)
                throw new InvalidOperationException("Invalid source path did not direct the user to data sources.");
        }
        finally { settings.Close(); }
        foreach (var warning in new[] { false, true })
        {
            using var icon = TrayIcon.CreateIcon(warning);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(Path.Combine(directory, warning ? "tray-warning.png" : "tray-normal.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(directory, "README.txt"), $"TokenPulse {Ui.Version}. Actual WPF renders with synthetic data, exported at 96 and 144 DPI. Wide cases explicitly measure the same visual tree at 1920/2560/3840 pixel viewports; maximized cases exercise real WindowState.Maximized on the CI desktop. Bounded content widths and horizontal centering are asserted for every capture. Maximize/restore and reopen-while-maximized are exercised. These do not replace physical mixed-DPI monitor testing. No user logs or settings were read or written.");
    }
    private static async Task ShowAndCapture(Window window, string directory, string name)
    {
        if (!window.IsVisible) window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        if (root.ActualWidth < 100 || root.ActualHeight < 100) throw new InvalidOperationException("Window did not lay out.");
        Save(root, directory, name);
    }
    private static void CaptureViewport(Window window, string directory, string name, Size size)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        // ContentPresenter removes its old visual lazily. Flush that removal before
        // arranging offscreen, otherwise its previous window size wins at layout time.
        window.UpdateLayout();
        var host = new Border { Child = root, Width = size.Width, Height = size.Height };
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        try
        {
            host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
            if (Math.Abs(root.ActualWidth - size.Width) > 1 || Math.Abs(root.ActualHeight - size.Height) > 1)
                throw new InvalidOperationException($"Viewport did not reach its requested size: {name}: {root.RenderSize}, expected {size}.");
            Save(root, directory, name);
        }
        finally
        {
            host.Child = null;
            window.Content = root; window.UpdateLayout();
        }
    }
    private static void Save(FrameworkElement root, string directory, string name)
    {
        var panel = Descendants<FrameworkElement>(root).Single(e => e.Name is "DashboardContent" or "SettingsContent");
        var bounds = panel.TransformToAncestor(root).TransformBounds(new Rect(panel.RenderSize));
        if (panel.ActualWidth > panel.MaxWidth + 1 || panel.ActualHeight > panel.MaxHeight + 1
            || Math.Abs((bounds.Left + bounds.Right) / 2 - root.ActualWidth / 2) > 2)
            throw new InvalidOperationException($"Content exceeded its bounds or lost centering: {name}, panel {bounds}, root {root.RenderSize}.");
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"{name}-{(int)(scale * 100)}.png")); encoder.Save(file);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
