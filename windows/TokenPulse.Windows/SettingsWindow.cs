using System.Globalization;

namespace TokenPulse.Windows;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings settings, Func<Settings, string?> save)
    {
        Title = $"TokenPulse 偏好设置 · {Ui.Version}"; Width = 540; Height = 680; MinWidth = 460; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ConfigureWindow(this);
        var root = new DockPanel { Name = "SettingsContent", Margin = new Thickness(20, 18, 20, 16) };
        var top = new StackPanel();
        top.Children.Add(Ui.Header(this, "偏好设置", "让 TokenPulse 适合你的工作方式"));
        var tabs = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
        top.Children.Add(new Border { Child = tabs, CornerRadius = new CornerRadius(9), Background = Ui.Brush("#EEEFF6"),
            Padding = new Thickness(2), Margin = new Thickness(0, 17, 0, 14) });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel();
        var error = Ui.Text("", 11, Ui.Brush("#B44C48")); error.Visibility = Visibility.Collapsed;
        error.Margin = new Thickness(0, 8, 0, 10); bottom.Children.Add(error);
        var buttons = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto, new(8), GridLength.Auto);
        Ui.Put(buttons, Ui.Text($"{Ui.Version} · 当前设备", 10, Ui.Muted), 0);
        var cancel = Ui.Button("取消", Close); cancel.IsCancel = true; Ui.Put(buttons, cancel, 1);
        var saveButton = Ui.Button("保存设置", () => { }, true); saveButton.IsDefault = true; Ui.Put(buttons, saveButton, 3);
        bottom.Children.Add(buttons); bottom.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var body = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        var sources = new StackPanel();
        var behavior = new StackPanel { Visibility = Visibility.Collapsed };
        body.Children.Add(sources); body.Children.Add(behavior);
        var scroll = new ScrollViewer { Name = "SettingsScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        foreach (var (title, page) in new[] { ("数据源", sources), ("运行偏好", behavior) })
        {
            var tab = new RadioButton { Content = title, GroupName = "SettingsPage", IsChecked = page == sources, Style = (Style)FindResource("Segment") };
            tab.Checked += (_, _) =>
            {
                sources.Visibility = page == sources ? Visibility.Visible : Visibility.Collapsed;
                behavior.Visibility = page == behavior ? Visibility.Visible : Visibility.Collapsed;
                scroll.ScrollToTop();
            };
            tabs.Children.Add(tab);
        }
        sources.Children.Add(Ui.Text("只扫描你开启的数据源。关闭后保留路径，随时可恢复。", 10.5, Ui.Muted)); Ui.Gap(sources, 12);
        var paths = new StackPanel();
        (TextBox Path, CheckBox Enabled) AddPath(string title, string description, string path, bool enabled, bool file = false)
        {
            var label = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto, new(12), GridLength.Auto);
            Ui.Put(label, Ui.Text(title, 11.5, Ui.Ink, FontWeights.SemiBold), 0);
            Ui.Put(label, Ui.Text(description, 10, Ui.Muted), 1);
            var toggle = new CheckBox { IsChecked = enabled, Style = (Style)FindResource("Switch"), ToolTip = "启用 " + title };
            System.Windows.Automation.AutomationProperties.SetName(toggle, "启用 " + title);
            Ui.Put(label, toggle, 3); paths.Children.Add(label); Ui.Gap(paths, 7);
            var row = Ui.Columns(new(1, GridUnitType.Star), new(7), GridLength.Auto);
            var box = new TextBox { Text = path ?? "", ToolTip = path, MinWidth = 140 };
            box.TextChanged += (_, _) => box.ToolTip = box.Text;
            System.Windows.Automation.AutomationProperties.SetName(box, title);
            var browse = Ui.Button("浏览", () =>
            {
                if (file)
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSONL 日志 (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*" };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
                }
                else
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog();
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
                }
            });
            void SetEnabled() { row.IsEnabled = toggle.IsChecked == true; row.Opacity = row.IsEnabled ? 1 : .45; }
            toggle.Checked += (_, _) => SetEnabled(); toggle.Unchecked += (_, _) => SetEnabled(); SetEnabled();
            Ui.Put(row, box, 0); Ui.Put(row, browse, 2); paths.Children.Add(row); Ui.Gap(paths, 15); return (box, toggle);
        }
        var claude = AddPath("Claude Code", "projects 日志目录", settings.ClaudeDirectory, settings.EnableClaude);
        var codex = AddPath("OpenAI Codex", "sessions 会话目录", settings.CodexDirectory, settings.EnableCodex);
        var antigravity = AddPath("Antigravity", "brain 日志目录", settings.AntigravityDirectory, settings.EnableAntigravity);
        var custom = AddPath("自定义事件", "JSONL 文件", settings.CustomEventsFile, settings.EnableCustom, true);
        paths.Children.Add(Ui.Text("支持 %USERPROFILE% 等环境变量；源文件始终只读。", 10, Ui.Muted));
        sources.Children.Add(Ui.Card(paths, new Thickness(14)));
        behavior.Children.Add(Ui.Text("高消耗提醒", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(behavior, 6);
        behavior.Children.Add(Ui.Text("近 5 分钟达到任一阈值时，面板与托盘显示橙色提醒。", 10.5, Ui.Muted)); Ui.Gap(behavior, 12);
        var alerts = new StackPanel();
        var limits = Ui.Columns(new(1, GridUnitType.Star), new(12), new(1, GridUnitType.Star));
        TextBox Limit(string label, string value, int column)
        {
            var panel = new StackPanel(); panel.Children.Add(Ui.Text(label, 11, Ui.Ink, FontWeights.Medium)); Ui.Gap(panel, 7);
            var input = new TextBox { Text = value };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            panel.Children.Add(input); Ui.Put(limits, panel, column); return input;
        }
        var tokenLimit = Limit("Token 阈值（整数）", settings.BurnRateTokens.ToString(CultureInfo.CurrentCulture), 0);
        var costLimit = Limit("等效费用阈值（USD）", settings.BurnRateUSD.ToString(CultureInfo.CurrentCulture), 2);
        alerts.Children.Add(limits); Ui.Gap(alerts, 10);
        alerts.Children.Add(Ui.Text("填 0 关闭对应提醒；两项均为 0 时关闭高消耗提醒。费用为估算值。", 10, Ui.Muted));
        var defaults = Ui.Button("恢复默认阈值", () =>
        {
            tokenLimit.Text = 250_000L.ToString(CultureInfo.CurrentCulture);
            costLimit.Text = 1.50.ToString(CultureInfo.CurrentCulture);
        });
        defaults.HorizontalAlignment = HorizontalAlignment.Left; defaults.Margin = new Thickness(0, 12, 0, 0);
        alerts.Children.Add(defaults); behavior.Children.Add(Ui.Card(alerts, new Thickness(14))); Ui.Gap(behavior, 18);
        behavior.Children.Add(Ui.Text("启动与本地模型", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(behavior, 10);
        var preferences = new StackPanel();
        CheckBox Toggle(string title, string description, bool enabled)
        {
            var row = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto);
            var labels = new StackPanel(); labels.Children.Add(Ui.Text(title, 11.5, Ui.Ink, FontWeights.Medium));
            var detail = Ui.Text(description, 10, Ui.Muted); detail.Margin = new Thickness(0, 4, 12, 0); labels.Children.Add(detail);
            Ui.Put(row, labels, 0);
            var toggle = new CheckBox { IsChecked = enabled, Style = (Style)FindResource("Switch"), ToolTip = title };
            System.Windows.Automation.AutomationProperties.SetName(toggle, title);
            Ui.Put(row, toggle, 1); preferences.Children.Add(row); return toggle;
        }
        var startup = Toggle("登录时启动", "在系统托盘中安静运行", settings.StartWithWindows); Ui.Gap(preferences, 16);
        var ollama = Toggle("Ollama 本地模型", "显示本机运行模型与显存占用", settings.EnableOllama);
        behavior.Children.Add(Ui.Card(preferences, new Thickness(14)));
        saveButton.Click += (_, _) =>
        {
            try
            {
                if (!long.TryParse(tokenLimit.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var tokenValue)
                    || !double.TryParse(costLimit.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var costValue))
                {
                    ((RadioButton)tabs.Children[1]).IsChecked = true;
                    throw new ArgumentException("请输入有效阈值：Token 使用整数，费用使用当前系统的小数格式；填 0 可关闭。");
                }
                try { new BurnRateThresholds(tokenValue, costValue).Validate(); }
                catch (ArgumentException) { ((RadioButton)tabs.Children[1]).IsChecked = true; throw; }
                string Expand(TextBox box) => Environment.ExpandEnvironmentVariables(box.Text.Trim());
                var candidate = settings with { ClaudeDirectory = Expand(claude.Path), CodexDirectory = Expand(codex.Path), AntigravityDirectory = Expand(antigravity.Path),
                    CustomEventsFile = Expand(custom.Path), EnableClaude = claude.Enabled.IsChecked == true, EnableCodex = codex.Enabled.IsChecked == true,
                    EnableAntigravity = antigravity.Enabled.IsChecked == true, EnableCustom = custom.Enabled.IsChecked == true,
                    BurnRateTokens = tokenValue, BurnRateUSD = costValue, StartWithWindows = startup.IsChecked == true, EnableOllama = ollama.IsChecked == true };
                try { candidate.Validate(); }
                catch (ArgumentException) { ((RadioButton)tabs.Children[0]).IsChecked = true; throw; }
                var message = save(candidate);
                if (message is null) DialogResult = true;
                else { error.Text = message; error.Visibility = Visibility.Visible; }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        };
        root.Children.Add(scroll); Content = Ui.Shell(root, 560, 760);
    }
}
