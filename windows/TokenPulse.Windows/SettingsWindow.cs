namespace TokenPulse.Windows;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings settings, Func<Settings, string?> save)
    {
        Title = "TokenPulse 设置"; Width = 620; Height = 550; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(MainWindow.Text("本地数据源", 20, FontWeights.Bold));
        panel.Children.Add(MainWindow.Text("填写绝对路径；支持 %USERPROFILE% 等环境变量。不会修改源日志。", 12));
        TextBox AddPath(string title, string path, bool file = false)
        {
            panel.Children.Add(MainWindow.Text(title, 12));
            var row = new DockPanel();
            var box = new TextBox { Text = path, Padding = new Thickness(5), MinWidth = 240 };
            var browse = MainWindow.Button("浏览…", () =>
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
            DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(box); panel.Children.Add(row);
            return box;
        }
        var claude = AddPath("Claude Code 日志目录", settings.ClaudeDirectory);
        var codex = AddPath("Codex 会话目录", settings.CodexDirectory);
        var antigravity = AddPath("Antigravity brain 目录", settings.AntigravityDirectory);
        var custom = AddPath("自定义事件文件", settings.CustomEventsFile, true);
        var startup = new CheckBox { Content = "登录 Windows 时启动", IsChecked = settings.StartWithWindows, Margin = new Thickness(0, 16, 0, 8) };
        var ollama = new CheckBox { Content = "查询本机 Ollama 运行模型", IsChecked = settings.EnableOllama, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(startup); panel.Children.Add(ollama);
        var error = MainWindow.Text(""); error.Foreground = Brushes.Firebrick; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MainWindow.Button("保存", () =>
        {
            string Expand(TextBox box) => Environment.ExpandEnvironmentVariables(box.Text.Trim());
            var candidate = settings with { ClaudeDirectory = Expand(claude), CodexDirectory = Expand(codex),
                AntigravityDirectory = Expand(antigravity), CustomEventsFile = Expand(custom),
                StartWithWindows = startup.IsChecked == true, EnableOllama = ollama.IsChecked == true };
            try
            {
                candidate.Validate();
                var message = save(candidate);
                if (message is null) DialogResult = true;
                else error.Text = message;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { error.Text = ex.Message; }
        }));
        buttons.Children.Add(MainWindow.Button("取消", Close)); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
