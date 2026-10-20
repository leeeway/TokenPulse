namespace TokenPulse.Windows;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings settings, Func<Settings, string?> save)
    {
        Title = "TokenPulse 偏好设置"; Width = 540; Height = 680; MinWidth = 460; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ConfigureWindow(this);
        var root = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };
        var header = Ui.Header(this, "偏好设置", "让 TokenPulse 适合你的工作方式");
        header.Margin = new Thickness(0, 0, 0, 20); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var bottom = new StackPanel();
        var error = Ui.Text("", 11, Ui.Brush("#B44C48")); error.Visibility = Visibility.Collapsed;
        error.Margin = new Thickness(0, 8, 0, 10); bottom.Children.Add(error);
        var buttons = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto, new(8), GridLength.Auto);
        Ui.Put(buttons, Ui.Text("设置仅保存在当前设备", 10, Ui.Muted), 0);
        var cancel = Ui.Button("取消", Close); cancel.IsCancel = true; Ui.Put(buttons, cancel, 1);
        var saveButton = Ui.Button("保存设置", () => { }, true); saveButton.IsDefault = true; Ui.Put(buttons, saveButton, 3);
        bottom.Children.Add(buttons); bottom.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var body = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        body.Children.Add(Ui.Text("数据源", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(body, 5);
        body.Children.Add(Ui.Text("选择 AI 工具的本地日志目录。源文件始终只读。", 10.5, Ui.Muted)); Ui.Gap(body, 12);
        var paths = new StackPanel();
        TextBox AddPath(string title, string description, string path, bool file = false)
        {
            var label = Ui.Columns(new(1, GridUnitType.Star), GridLength.Auto);
            Ui.Put(label, Ui.Text(title, 11.5, Ui.Ink, FontWeights.SemiBold), 0);
            Ui.Put(label, Ui.Text(description, 10, Ui.Muted), 1); paths.Children.Add(label); Ui.Gap(paths, 7);
            var row = Ui.Columns(new(1, GridUnitType.Star), new(7), GridLength.Auto);
            var box = new TextBox { Text = path, ToolTip = path, MinWidth = 180 };
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
            Ui.Put(row, box, 0); Ui.Put(row, browse, 2); paths.Children.Add(row); Ui.Gap(paths, 15); return box;
        }
        var claude = AddPath("Claude Code", "projects 日志目录", settings.ClaudeDirectory);
        var codex = AddPath("OpenAI Codex", "sessions 会话目录", settings.CodexDirectory);
        var antigravity = AddPath("Antigravity", "brain 日志目录", settings.AntigravityDirectory);
        var custom = AddPath("自定义事件", "custom_events.jsonl 文件", settings.CustomEventsFile, true);
        paths.Children.Add(Ui.Text("支持 %USERPROFILE% 等环境变量；路径可暂不存在。", 10, Ui.Muted));
        body.Children.Add(Ui.Card(paths, new Thickness(14))); Ui.Gap(body, 18);
        body.Children.Add(Ui.Text("运行偏好", 12, Ui.Ink, FontWeights.SemiBold)); Ui.Gap(body, 10);
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
        body.Children.Add(Ui.Card(preferences, new Thickness(14)));
        saveButton.Click += (_, _) =>
        {
            string Expand(TextBox box) => Environment.ExpandEnvironmentVariables(box.Text.Trim());
            var candidate = settings with { ClaudeDirectory = Expand(claude), CodexDirectory = Expand(codex), AntigravityDirectory = Expand(antigravity),
                CustomEventsFile = Expand(custom), StartWithWindows = startup.IsChecked == true, EnableOllama = ollama.IsChecked == true };
            try
            {
                candidate.Validate();
                var message = save(candidate);
                if (message is null) DialogResult = true;
                else { error.Text = message; error.Visibility = Visibility.Visible; }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
        };
        root.Children.Add(new ScrollViewer { Name = "SettingsScroll", Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = Ui.Shell(root);
    }
}
