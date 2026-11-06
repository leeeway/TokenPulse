using System.Globalization;
using System.Text;
using System.Text.Json;
using TokenPulse.Core;

internal static class QuotaLedgerTests
{
    public static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS Ledger: " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL Ledger: " + name + ": " + ex); }
        }
        void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
        void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-10, $"Expected {expected}; got {actual}");
        var start = DateTimeOffset.Parse("2026-10-20T07:07:27Z", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse("2026-10-27T07:07:27Z", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.Parse("2026-10-21T15:41:03Z", CultureInfo.InvariantCulture);
        UsageEvent[] Events() => [
            UsageEvent.Create("secret-event-a", Providers.Codex, "gpt-5", start.AddHours(1), "secret-session-a", 1000, 0, 9000, 100, 20),
            UsageEvent.Create("secret-event-b", Providers.Codex, "gpt-5", now.AddHours(-1), "secret-session-b", 2000, 500, 4000, 200, 30)];
        QuotaObservation[] Observations() => [new(start.AddHours(1), "secondary", 10080, end, 75, "team"),
            new(now.AddHours(-2), "primary", 10080, end, 100, "team")];
        ScanResult Scan(IReadOnlyList<UsageEvent>? events = null, IReadOnlyList<QuotaObservation>? observations = null,
            SourceState state = SourceState.Ready) => new(events ?? Events(), [], [new(Providers.Codex, "private-path", state, "synthetic")])
            { NativeCodexEvents = events ?? Events(), QuotaObservations = observations ?? Observations() };
        string Log(string timestamp, string windows, string? usage = null) => "{\"timestamp\":\"" + timestamp
            + "\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{" + windows + "}"
            + (usage is null ? "" : ",\"info\":{\"total_token_usage\":" + usage + "}") + "}}";
        var weekly = $"\"used_percent\":75,\"window_minutes\":10080,\"resets_at\":{end.ToUnixTimeSeconds()}";
        ParsedLog Parse(string log) => LogParser.Parse(new StringReader(log), Providers.Codex, "secret-session", now);

        Test("strict observations reject missing or invalid evidence and accept either weekly role", () =>
        {
            var good = Parse(Log(start.AddHours(1).ToString("O"), "\"secondary\":{" + weekly + "}"));
            Check(good.QuotaObservations.Count == 1 && good.RateLimit is null);
            Check(Parse(Log(start.AddHours(1).ToString("O"), "\"primary\":{" + weekly + "}")).QuotaObservations.Count == 1);
            foreach (var content in new[] {
                Log("bad", "\"primary\":{" + weekly + "}"),
                Log("2026-10-20T08:00:00", "\"primary\":{" + weekly + "}"),
                Log(start.ToString("O"), "\"primary\":{" + weekly.Replace("\"used_percent\":75,", "") + "}"),
                Log(start.ToString("O"), "\"primary\":{" + weekly.Replace("\"window_minutes\":10080,", "") + "}"),
                Log(start.ToString("O"), "\"primary\":{" + weekly.Replace("\"resets_at\":" + end.ToUnixTimeSeconds(), "\"resets_at\":0") + "}"),
                Log(start.ToString("O"), "\"primary\":{" + weekly.Replace("\"used_percent\":75", "\"used_percent\":101") + "}") })
                Check(Parse(content).QuotaObservations.Count == 0, content);
        });
        Test("shared fixture uses UTC half-open bounds and keeps events after 100 percent", () =>
        {
            var events = Events().Concat(new[] {
                UsageEvent.Create("before", Providers.Codex, "gpt-5", start.AddTicks(-1), "excluded", 999,0,0,0),
                UsageEvent.Create("at-end", Providers.Codex,"gpt-5",end,"excluded",999,0,0,0),
                UsageEvent.Create("at-snapshot", Providers.Codex,"gpt-5",now,"excluded",999,0,0,0),
                UsageEvent.Create("undated", Providers.Codex,"gpt-5",start,"excluded",999,0,0,0) with {HasKnownTimestamp=false} }).ToArray();
            var report = QuotaReports.Build(events,start,end,now,"current",Observations()[1],"synthetic",Observations(),TimeZoneInfo.Utc);
            Check(report.Totals.Total == 16800 && report.Totals.Input == 3000 && report.Totals.CacheWrite == 500
                && report.Totals.CacheRead == 13000 && report.Totals.Output == 300 && report.Totals.Reasoning == 50);
            Check(report.Totals.Events == 2 && report.Totals.Sessions == 2 && report.Days.Count == 2);
            Near(.0159,report.Totals.EstimatedUSD); Near(.0195,report.Totals.SavedUSD);
            Check(report.FirstObserved100AtUtc == now.AddHours(-2));
        });
        Test("native/custom Codex separation survives scanner cache and persistence", () =>
        {
            using var directory = new LedgerDirectory();
            Directory.CreateDirectory(directory.Root + "/native");
            var native = directory.Root + "/native/private-file.jsonl";
            File.WriteAllText(native,Log(start.AddHours(1).ToString("O"),"\"secondary\":{"+weekly+"}","{\"input_tokens\":10000,\"cached_input_tokens\":9000,\"output_tokens\":100}"));
            var custom = UsageEvent.Create("secret-custom-id",Providers.Codex,"custom-model",start.AddHours(1),"secret-custom-session",999999,0,0,0);
            var customPath=directory.Root+"/custom.jsonl";
            File.WriteAllText(customPath,JsonSerializer.Serialize(custom,new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
            var settings = new Settings {CodexDirectory=directory.Root+"/native",CustomEventsFile=customPath,EnableClaude=false,EnableAntigravity=false};
            var scanner=new LogScanner(); var scan=scanner.Scan(settings);var again=scanner.Scan(settings);
            Check(scan.Events.Count==2 && scan.NativeCodexEvents.Count==1 && scan.QuotaObservations.Count==1);
            Check(again.NativeCodexEvents.Count==1 && again.QuotaObservations.Count==1);
            var store=new QuotaLedgerStore(directory.File);var session=new QuotaLedgerSession(store);session.Update(scan,true,now);
            Check(session.View().Current!.Totals.Total==10100);
            var text=File.ReadAllText(directory.File);
            foreach(var secret in new[]{"secret-custom-id","secret-custom-session","secret-session","private-file",directory.Root,"sessionId","prompt"})
                Check(!text.Contains(secret,StringComparison.OrdinalIgnoreCase),"Persisted private field: "+secret);
        });
        Test("current rescans replace totals after truncation rather than adding old summaries",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            session.Update(Scan(),true,now);Check(session.View().Current!.Totals.Total==16800);
            session.Update(Scan([Events()[1]],Observations()),true,now.AddMinutes(1));
            Check(session.View().Current!.Totals.Total==6700 && session.View().Cycles.Single().Report.Totals.Total==6700);
            session.Update(Scan([],[]),true,now.AddMinutes(2));Check(session.View().Current is null);
        });
        Test("closed archives remain frozen when logs disappear or change",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            session.Update(Scan(),true,end.AddSeconds(1));var frozen=session.View().Cycles.Single();
            Check(frozen.Archived && frozen.Report.Totals.Total==16800);
            session.Update(Scan([],[]),true,end.AddHours(1));Check(session.View().Cycles.Single()==frozen);
            session.Update(Scan(Events().Concat(Events()).ToArray()),true,end.AddHours(2));Check(session.View().Cycles.Single()==frozen);
        });
        Test("disabled and failed native sources retain history without a current report",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            session.Update(Scan(),true,now);session.Update(Scan(state:SourceState.Disabled),false,now.AddMinutes(1));
            Check(session.View().Current is null && session.View().Cycles.Count==1 && !session.View().NativeSourceAvailable);
            session.Update(Scan(state:SourceState.Error),true,now.AddMinutes(2));Check(session.View().Current is null);
            session.Suspend(false);Check(!session.View().NativeSourceAvailable);
        });
        Test("clear retains current and prevents automatic ended-period refill until rebuild",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            var pastEnd=start.AddDays(-1);var old=new QuotaObservation(pastEnd.AddDays(-1),"secondary",10080,pastEnd,99,"team");
            var scan=Scan(observations:Observations().Append(old).ToArray());session.Update(scan,true,now);
            Check(session.View().Cycles.Count==2);session.ClearEndedHistory();Check(session.View().Cycles.Count==1 && session.View().Current is not null);
            session.Update(scan,true,now.AddMinutes(1));Check(session.View().Cycles.Count==1);
            session=new QuotaLedgerSession(new(directory.File));session.Update(scan,true,now.AddMinutes(2));Check(session.View().Cycles.Count==1);
            session.Rebuild();Check(session.View().Cycles.Count==2 && session.View().ClearedEndedBeforeUtc is null);
        });
        Test("retention prunes ended cycles older than 90 days",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            var ancientEnd=now.AddDays(-91);var old=new QuotaObservation(ancientEnd.AddDays(-1),"primary",10080,ancientEnd,75,"team");
            session.Update(Scan(observations:Observations().Append(old).ToArray()),true,now);Check(session.View().Cycles.Count==1);
        });
        Test("corrupt JSON remains untouched until an explicit rebuild keeps a backup",()=>
        {
            using var directory=new LedgerDirectory();File.WriteAllText(directory.File,"{broken original}");
            var session=new QuotaLedgerSession(new(directory.File));session.Update(Scan(),true,now);
            Check(session.View().StoreCorrupt && File.ReadAllText(directory.File)=="{broken original}");
            try{session.ClearEndedHistory();throw new Exception("Expected corrupt protection");}catch(IOException){}
            session.Rebuild();Check(!session.View().StoreCorrupt && session.View().Cycles.Count==1);
            Check(Directory.GetFiles(directory.Root,"ledger.json.backup-*").Any(p=>File.ReadAllText(p)=="{broken original}"));
        });
        Test("same reset across primary and secondary is one cycle and overlap warns",()=>
        {
            using var directory=new LedgerDirectory();var session=new QuotaLedgerSession(new(directory.File));
            var observations=Observations().Append(Observations()[1] with {WindowRole="secondary"}).ToArray();
            session.Update(Scan(observations:observations),true,now);Check(session.View().Cycles.Count==1);
            observations=observations.Append(Observations()[1] with {ResetsAtUtc=end.AddHours(1)}).ToArray();
            session.Update(Scan(observations:observations),true,now.AddMinutes(1));
            Check(session.View().Current!.Coverage.Contains("重叠周期") && session.View().Cycles.Count==2);
        });
        Test("old pricing version and basis load unchanged and custom report bounds validate",()=>
        {
            using var directory=new LedgerDirectory();var store=new QuotaLedgerStore(directory.File);var session=new QuotaLedgerSession(store);
            session.Update(Scan(),true,now);var loaded=store.Load().Document;
            var entry=loaded.Cycles.Single();var legacy=entry with {Report=entry.Report with {PricingVersion="legacy-v0",PricingBasis="Frozen legacy quote"}};
            store.Save(loaded with {Cycles=[legacy],PricingVersion="legacy-v0"});
            Check(store.Load().Error is null && store.Load().Document.Cycles.Single().Report.PricingBasis=="Frozen legacy quote");
            var report=session.Custom(start,now.AddHours(1));Check(report.StatisticsEndUtc==now && report.Quota is null);
            try{session.Custom(end,start);throw new Exception("Expected invalid interval");}catch(ArgumentException){}
            var generation=session.Generation;session.Suspend(false);session.Update(Scan(),true,now,expectedGeneration:generation);
            Check(!session.View().NativeSourceAvailable,"Stale source result revived suspended state");
        });
        Test("frozen zone determines local days without changing UTC bounds",()=>
        {
            var zone=TimeZoneInfo.CreateCustomTimeZone("Example UTC plus 8",TimeSpan.FromHours(8),"Example","Example");
            var midnight=DateTimeOffset.Parse("2026-10-20T18:00:00Z",CultureInfo.InvariantCulture);
            var report=QuotaReports.Build([UsageEvent.Create("local",Providers.Codex,"gpt-5",midnight,"session",1,0,0,0)],start,end,now,"custom",null,"synthetic",timeZone:zone);
            Check(report.Days.Single().Date==new DateTime(2026,10,21) && report.TimeZoneId==zone.Id && report.UtcOffsetMinutes==480);
            Check(report.StartUtc==start && report.StartLocal.EndsWith("+08:00",StringComparison.Ordinal));
        });
        Test("HTML CSV atomic exports escape untrusted models and preserve frozen amounts",()=>
        {
            using var directory=new LedgerDirectory();
            var model="=SUM(1,2)<script>alert(1)</script>";
            var item=UsageEvent.Create("secret-export-event",Providers.Codex,model,start.AddHours(1),"secret-export-session",1000,0,0,100);
            var report=QuotaReports.Build([item],start,end,now,"custom",null,"固定覆盖说明") with {PricingBasis="Frozen custom pricing basis"};
            var first=QuotaReportExport.Export(report,directory.Root);var second=QuotaReportExport.Export(report,directory.Root);
            Check(first!=second && Directory.Exists(first) && Directory.Exists(second));
            var html=File.ReadAllText(first+"/report.html");var csv=File.ReadAllBytes(first+"/report.csv");var csvText=Encoding.UTF8.GetString(csv);
            Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>") && html.Contains("Frozen custom pricing basis"));
            Check(csv.Take(3).SequenceEqual(new byte[]{239,187,191}) && csvText.Contains("'=SUM(1,2)"));
            foreach(var secret in new[]{"secret-export-event","secret-export-session",directory.Root})Check(!html.Contains(secret)&&!csvText.Contains(secret));
            Check(!Directory.GetDirectories(directory.Root,".tokenpulse-report-*.tmp").Any());
            var blocked=directory.Root+"/file";File.WriteAllText(blocked,"unchanged");
            try{QuotaReportExport.Export(report,blocked);throw new Exception("Expected write failure");}catch(IOException){}
            Check(File.ReadAllText(blocked)=="unchanged");
        });
        return (passed,failed);
    }
    private sealed class LedgerDirectory:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"tokenpulse-ledger-test-"+Guid.NewGuid());
        public string File=>Path.Combine(Root,"ledger.json");
        public LedgerDirectory()=>Directory.CreateDirectory(Root);
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
