using System.Security.Principal;
using System.Text.Json;
using System.Windows.Threading;

namespace TokenPulse.Windows;

internal sealed class App : Application
{
    private readonly SettingsStore store = new();
    private readonly LogScanner scanner = new();
    private readonly OllamaClient ollama = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer resetTimer = new();
    private DateTimeOffset? scheduledReset;
    private IReadOnlyList<QuotaObservation> quotas = [];
    private bool hasUnverifiedQuota;
    private bool quotaRefreshPending;
    private DateTimeOffset lastRefreshStartedAt;
    private QuotaAlerts quotaAlerts = new();
    private string QuotaAlertsPath => Path.Combine(Path.GetDirectoryName(store.FilePath)!, "quota-alerts.json");
    private Settings settings = new();
    private MainWindow? dashboard;
    private TrayIcon? tray;
    private QuotaLedgerSession? ledger;
    private QuotaLedgerWindow? ledgerWindow;
    private Mutex? instance;
    private bool ownsMutex, refreshing, settingsOpen;
    private ScanResult? scan;
    private Settings? scanSettings;
    private OllamaResult localModels = new([], "正在查询 Ollama…");
    private string? settingsWarning;
    private long renderRevision;
    private CancellationTokenSource? rendering;

    [STAThread]
    public static int Main() => new App().Run();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TokenPulse;component/Theme.xaml", UriKind.Relative) });
        if (e.Args.Contains("--visual-test"))
        {
            var index = Array.IndexOf(e.Args, "--visual-test");
            var directory = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(AppContext.BaseDirectory, "ui-previews");
            Dispatcher.BeginInvoke(async () =>
            {
                try { await UiSnapshots.CaptureAsync(directory); Shutdown(0); }
                catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); Shutdown(1); }
            });
            return;
        }
        var smoke = e.Args.Contains("--smoke-test");
        instance = new Mutex(true, @"Local\TokenPulse.Windows." + WindowsIdentity.GetCurrent().User?.Value + (smoke ? ".Smoke" : ""), out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show($"TokenPulse 已在运行。\n\n如果正在更新，请先从原托盘菜单选择「退出」，再启动新版 {Ui.Version}。", $"TokenPulse {Ui.Version}"); Shutdown(); return;
        }
        (settings, settingsWarning) = store.Load();
        try { settings = settings with { StartWithWindows = StartupRegistration.Read() is not null }; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { settingsWarning = "无法读取开机启动状态：" + ex.Message; }
        if (!smoke)
        {
            ledger = new QuotaLedgerSession();
            quotaAlerts = LoadQuotaAlerts();
        }
        dashboard = new MainWindow(() => _ = RefreshAsync(), OpenSettings, () => _ = RenderAsync(), OpenLedger);
        MainWindow = dashboard;
        tray = new TrayIcon(OpenDashboard, () => _ = RefreshAsync(), OpenSettings, () => Shutdown(), OpenLedger);
        dashboard.ShowError(settingsWarning);
        if (smoke)
        {
            // Exercise real WPF controls and tray initialization on Windows without reading user logs.
            var now = DateTimeOffset.Now;
            scan = new([UsageEvent.Create("smoke", Providers.Codex, "gpt-5-codex", now, "smoke", 1000, 0, 500, 100)], [], []);
            scanSettings = settings;
            localModels = new([], "Smoke test");
            _ = RenderAsync(); dashboard.OpenNearTray();
            var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            smokeTimer.Tick += (_, _) => { smokeTimer.Stop(); Shutdown(dashboard.HasRendered ? 0 : 1); };
            smokeTimer.Start();
            return;
        }
        resetTimer.Tick += async (_, _) =>
        {
            resetTimer.Stop();
            var deadline = scheduledReset;
            scheduledReset = null;
            var now = DateTimeOffset.UtcNow;
            ScheduleQuotaReset(now);
            if (deadline is { } due && now >= due && lastRefreshStartedAt < due)
                await RefreshAsync(queueIfBusy: true);
        };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        if (!e.Args.Contains("--background")) dashboard.OpenNearTray();
        _ = RefreshAsync();
    }
    private async Task RefreshAsync(bool queueIfBusy = false)
    {
        if (stopping.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow;
        UpdateQuotaTime(now);
        ledgerWindow?.AdvanceQuotaClock(now);
        if (refreshing)
        {
            quotaRefreshPending |= queueIfBusy;
            return;
        }
        lastRefreshStartedAt = now;
        refreshing = true;
        dashboard?.SetRefreshing(true);
        var currentSettings = settings;
        var ledgerGeneration = ledger?.Generation;
        try
        {
            await RefreshPipeline.RunAsync(
                () => Task.Run(() => scanner.Scan(currentSettings, stopping.Token), stopping.Token),
                () => currentSettings.EnableOllama ? ollama.ReadAsync(stopping.Token)
                    : Task.FromResult(new OllamaResult([], "Ollama 查询已关闭")),
                async result =>
                {
                    if (currentSettings != settings || stopping.IsCancellationRequested) return;
                    var observedAt = DateTimeOffset.UtcNow;
                    if (ledger is { } session)
                        await Task.Run(() => session.Update(result, currentSettings.EnableCodex, observedAt,
                            expectedGeneration: ledgerGeneration), stopping.Token);
                    if (currentSettings != settings || stopping.IsCancellationRequested) return;
                    scan = result; scanSettings = currentSettings;
                    quotas = currentSettings.EnableCodex ? result.LatestQuotas(observedAt) : [];
                    hasUnverifiedQuota = currentSettings.EnableCodex && result.RateLimits.Any(rate => rate.Provider == Providers.Codex);
                    var shownAt = DateTimeOffset.UtcNow;
                    UpdateQuotaTime(shownAt);
                    ScheduleQuotaReset(shownAt);
                    ledgerWindow?.Refresh();
                    if (currentSettings.EnableQuotaNotifications) NotifyQuota(shownAt);
                    localModels = new([], currentSettings.EnableOllama ? "正在查询 Ollama…" : "Ollama 查询已关闭");
                    dashboard?.ShowError(settingsWarning);
                    await RenderAsync();
                },
                async models =>
                {
                    if (currentSettings != settings || stopping.IsCancellationRequested) return;
                    localModels = models;
                    await RenderAsync();
                });
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { dashboard?.ShowError("刷新失败，将在下一轮重试：" + ex.Message); }
        finally
        {
            refreshing = false;
            dashboard?.SetRefreshing(false);
            var refreshAgain = quotaRefreshPending || currentSettings != settings;
            quotaRefreshPending = false;
            if (refreshAgain && !stopping.IsCancellationRequested) _ = RefreshAsync();
        }
    }
    private void UpdateQuotaTime(DateTimeOffset now)
    {
        dashboard?.UpdateQuota(quotas, now, hasUnverifiedQuota);
        tray?.UpdateQuota(quotas, now);
    }

    private void ScheduleQuotaReset(DateTimeOffset now)
    {
        var next = quotas.Where(o => !o.IsExpired(now)).Select(o => (DateTimeOffset?)o.ResetsAtUtc).Min();
        if (next == scheduledReset) return;
        resetTimer.Stop();
        scheduledReset = next;
        if (next is not { } deadline) return;
        // DispatcherTimer takes milliseconds bounded by Int32.
        resetTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp((deadline - now).TotalMilliseconds, 1, int.MaxValue));
        resetTimer.Start();
    }

    private QuotaAlerts LoadQuotaAlerts()
    {
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(QuotaAlertsPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private void NotifyQuota(DateTimeOffset now)
    {
        if (tray is null) return;
        var alerts = quotaAlerts.Claim(quotas, now);
        if (alerts.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QuotaAlertsPath)!);
            var temporary = QuotaAlertsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(quotaAlerts.NotifiedCycles));
            File.Move(temporary, QuotaAlertsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In-memory claims still suppress duplicates in the running app.
        }
        tray.NotifyQuota(alerts, now);
    }

    private void OpenDashboard()
    {
        UpdateQuotaTime(DateTimeOffset.UtcNow);
        dashboard?.OpenNearTray();
    }

    private async Task RenderAsync()
    {
        if (scan is null || dashboard is null || scanSettings != settings) return;
        rendering?.Cancel();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        rendering = work;
        var revision = ++renderRevision;
        var currentScan = scan;
        var currentModels = localModels;
        var currentWindow = dashboard.SelectedWindow;
        var thresholds = settings.AlertThresholds();
        var now = DateTimeOffset.Now;
        try
        {
            var snapshots = await Task.Run(() =>
            {
                var selected = Aggregation.Build(currentScan, currentWindow, now, cancellationToken: work.Token) with { AlertThresholds = thresholds };
                var today = currentWindow == TimeWindow.Today ? selected :
                    Aggregation.Build(currentScan, TimeWindow.Today, now, cancellationToken: work.Token) with { AlertThresholds = thresholds };
                return (selected, today);
            }, work.Token);
            // A newer scan or period selection must win even if an earlier job finishes last.
            if (revision != renderRevision || stopping.IsCancellationRequested) return;
            dashboard.Render(snapshots.selected, currentScan, currentModels);
            tray?.Update(snapshots.today);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (revision == renderRevision && !stopping.IsCancellationRequested)
                dashboard.ShowError("统计失败，将在下一轮重试：" + ex.Message);
        }
        finally { if (ReferenceEquals(rendering, work)) rendering = null; }
    }
    private void OpenLedger()
    {
        if (ledger is null) return;
        if (ledgerWindow is null)
        {
            ledgerWindow = new QuotaLedgerWindow(ledger, () => _ = RefreshAsync(queueIfBusy: true), () => DateTimeOffset.UtcNow);
            ledgerWindow.Closed += (_, _) => ledgerWindow = null;
        }
        ledgerWindow.Refresh();
        ledgerWindow.Show();
        if (ledgerWindow.WindowState == WindowState.Minimized) ledgerWindow.WindowState = WindowState.Normal;
        ledgerWindow.Activate();
        _ = RefreshAsync(queueIfBusy: true);
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
            rendering?.Cancel();
            renderRevision++;
            if (candidate.EnableCodex != settings.EnableCodex || candidate.CodexDirectory != settings.CodexDirectory)
            {
                quotas = [];
                hasUnverifiedQuota = false;
                UpdateQuotaTime(DateTimeOffset.UtcNow);
                ScheduleQuotaReset(DateTimeOffset.UtcNow);
            }
            settings = candidate; settingsWarning = null;
            ledger?.Suspend(candidate.EnableCodex);
            ledgerWindow?.Refresh();
            dashboard?.SetDataPending();
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
        timer.Stop(); resetTimer.Stop(); stopping.Cancel();
        if (dashboard is not null) { dashboard.AllowClose = true; dashboard.Close(); }
        ledgerWindow?.Close();
        tray?.Dispose(); ollama.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
