using System.Security.Principal;
using System.Windows.Threading;

namespace TokenPulse.Windows;

internal sealed class App : Application
{
    private readonly SettingsStore store = new();
    private readonly LogScanner scanner = new();
    private readonly OllamaClient ollama = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private Settings settings = new();
    private MainWindow? dashboard;
    private TrayIcon? tray;
    private Mutex? instance;
    private bool ownsMutex, refreshing, settingsOpen;
    private ScanResult? scan;
    private OllamaResult localModels = new([], "正在查询 Ollama…");
    private string? settingsWarning;

    [STAThread]
    public static int Main() => new App().Run();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var smoke = e.Args.Contains("--smoke-test");
        instance = new Mutex(true, @"Local\TokenPulse.Windows." + WindowsIdentity.GetCurrent().User?.Value + (smoke ? ".Smoke" : ""), out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("TokenPulse 已在运行，请点击右下角托盘图标。", "TokenPulse"); Shutdown(); return;
        }
        (settings, settingsWarning) = store.Load();
        try { settings = settings with { StartWithWindows = StartupRegistration.Read() is not null }; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { settingsWarning = "无法读取开机启动状态：" + ex.Message; }
        dashboard = new MainWindow(() => _ = RefreshAsync(), OpenSettings, Render);
        MainWindow = dashboard;
        tray = new TrayIcon(dashboard.OpenNearTray, () => _ = RefreshAsync(), OpenSettings, () => Shutdown());
        dashboard.ShowError(settingsWarning);
        if (smoke)
        {
            // Exercise real WPF controls and tray initialization on Windows without reading user logs.
            var now = DateTimeOffset.Now;
            scan = new([UsageEvent.Create("smoke", Providers.Codex, "gpt-5-codex", now, "smoke", 1000, 0, 500, 100)], [], []);
            localModels = new([], "Smoke test");
            Render(); dashboard.OpenNearTray();
            var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            smokeTimer.Tick += (_, _) => { smokeTimer.Stop(); Shutdown(); };
            smokeTimer.Start();
            return;
        }
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        if (!e.Args.Contains("--background")) dashboard.OpenNearTray();
        _ = RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        if (refreshing || stopping.IsCancellationRequested) return;
        refreshing = true;
        var currentSettings = settings;
        try
        {
            var result = await Task.Run(() => scanner.Scan(currentSettings, stopping.Token), stopping.Token);
            var models = currentSettings.EnableOllama ? await ollama.ReadAsync(stopping.Token) : new OllamaResult([], "Ollama 查询已关闭");
            if (currentSettings != settings || stopping.IsCancellationRequested) return;
            scan = result; localModels = models;
            dashboard?.ShowError(settingsWarning);
            Render();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { dashboard?.ShowError("刷新失败，将在下一轮重试：" + ex.Message); }
        finally
        {
            refreshing = false;
            if (currentSettings != settings && !stopping.IsCancellationRequested) _ = RefreshAsync();
        }
    }
    private void Render()
    {
        if (scan is null || dashboard is null) return;
        var now = DateTimeOffset.Now;
        dashboard.Render(Aggregation.Build(scan, dashboard.SelectedWindow, now), scan, localModels);
        tray?.Update(Aggregation.Build(scan, TimeWindow.Today, now));
    }
    private void OpenSettings()
    {
        if (settingsOpen || dashboard is null) return;
        settingsOpen = true;
        try
        {
            dashboard.OpenNearTray();
            var window = new SettingsWindow(settings, SaveSettings) { Owner = dashboard };
            if (window.ShowDialog() == true) _ = RefreshAsync();
        }
        finally { settingsOpen = false; }
    }
    private string? SaveSettings(Settings candidate)
    {
        object? previous = null;
        var startupChanged = false;
        try
        {
            candidate.Validate();
            if (candidate.StartWithWindows != settings.StartWithWindows)
            {
                previous = StartupRegistration.Read();
                StartupRegistration.Apply(candidate.StartWithWindows);
                startupChanged = true;
            }
            store.Save(candidate);
            settings = candidate; settingsWarning = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (startupChanged)
            {
                try { StartupRegistration.Write(previous); }
                catch (Exception rollback) { return $"设置未保存：{ex.Message}；启动设置恢复失败：{rollback.Message}"; }
            }
            return "设置未保存：" + ex.Message;
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop(); stopping.Cancel();
        if (dashboard is not null) { dashboard.AllowClose = true; dashboard.Close(); }
        tray?.Dispose(); ollama.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
