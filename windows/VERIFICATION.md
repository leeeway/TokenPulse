# Windows implementation verification

Status recorded on 2026-10-20. Host: macOS arm64. SDK used for C#: .NET 10.0.402 installed temporarily outside the repository.

## 2026-10-21 compact details: Windows 0.4.2

- Quota cards show local reset date/time directly, with explicit missing-time and expired-record labels. Quota availability and reset time are never inferred.
- Product cards and their model details are ordered by the latest reliable usage timestamp within the selected period and display at most three items each. Unknown timestamps sort last; equal times use totals and names for deterministic ordering. Full aggregates and CSV retain every source/model.
- Added regression coverage for out-of-order events, low-volume recent activity preceding older high-volume usage, unknown dates, period changes, ties and uncapped CSV. WPF checks cover the visible reset label, missing reset, three-card/three-model limits, and the actual compact view. Desktop Windows 11 mixed-DPI/tray behavior remains subject to the manual checklist.

## 2026-10-21 fixes: Windows 0.4.1

- Confirmed that appending unrelated content to an undated log moved 300,000 historical tokens into today's totals and triggered the five-minute alert. Native events now carry internal timestamp reliability metadata. Unknown-time usage is retained only in All totals/model summaries/export; calendar windows, trend and burn alerts exclude it, including after append, cache reuse and restart. The dashboard and source details explain the exclusion. The custom JSONL contract is unchanged.
- Confirmed that wholly malformed JSONL appeared as successfully read with zero usage. Parsed-file diagnostics now survive caching and report damaged JSON separately from a syntactically incomplete final object. Source details report file/usage counts, skipped malformed lines and pending final lines. Valid unrelated JSON is not classified as corruption; diagnostics do not claim full third-party schema validation.
- Added four regression groups covering all native providers, valid/invalid timestamps, every period, append/restart, serialization compatibility, incomplete JSON prefixes, cache reuse, completion and truncation. Added a WPF data-quality scenario with warning text and suppressed-alert assertions.
- Local core tests: **30 passed**. Existing Mac tests: **3 passed**; Mac Release packaging and code-signature verification passed without changing Mac sources or packaging scripts. The local Mac bundle was rebuilt and restarted separately; Windows 0.4.1 remains a Windows-only executable.
- Final WPF cross-compilation and Windows CI/runtime checks are recorded in the delivery message. Physical Windows 11 mixed-DPI/tray validation remains subject to the manual checklist.

## 2026-10-21 evolution: Windows 0.4.0

- Added independent persistent switches for the four log sources. Disabled sources are not accessed, are excluded from statistics, and have their cached parses evicted. Re-enabling reparses history. Status distinguishes disabled from missing/error; the empty dashboard links directly to settings.
- Added configurable five-minute token/cost thresholds with independent zero-to-disable semantics. Both dashboard and tray use the same threshold-bearing snapshot; defaults retain 0.3.0 behavior.
- Split settings into source and behavior tabs, preserving unsaved edits. Only enabled paths require validation. Threshold input rejects negatives/non-finite values, and a scoped reset restores only the default thresholds.
- Saving settings cancels stale aggregation, tags scans with the settings used to read them, and disables export until a matching scan is displayed.
- Core regression additions cover all 16 source combinations, disabled invalid paths, cache invalidation/re-enable, old JSON defaults, persistence, threshold boundaries and independent disabling. WPF scenarios exercise disabled-source and disabled-alert states, tab persistence, source input disabling, invalid threshold rejection, defaults restoration, and the settings callback.
- [Windows CI for `16b9561`](https://github.com/leeeway/TokenPulse/actions/runs/39527572441) passed 26 core tests, self-contained packaging, actual WPF/tray startup and visual scenarios. The generated dashboard/settings screenshots were inspected and saved in `docs/`. [Independent macOS CI](https://github.com/leeeway/TokenPulse/actions/runs/39527572542) also passed; local Swift tests (3) and Release build passed.
- Windows 11 physical mixed-DPI, tray/Explorer recovery and file-dialog interaction still require the manual checklist. Existing Mac sources/build configuration are unchanged.

## 2026-10-21 review: Windows 0.3.0

Review scope: refresh/aggregation, dashboard state, quota freshness and useful drill-downs. Existing macOS files are outside this change.

- **Responsiveness:** `App.Render` previously aggregated twice on the UI thread, and each aggregation repeatedly traversed history for providers and each trend day. Aggregation now accumulates totals, models and days in one event pass, runs on a worker, reuses today's snapshot, and cancels superseded work. A revision guard prevents older completions from replacing newer period selections.
- **Quota correctness:** previously the old percentage remained visually current after the recorded reset. Expired windows now explicitly wait for a new log record; no allowance is inferred. Expiry is evaluated independently for primary/secondary quotas and updates even when logs are unchanged.
- **Refresh interaction:** unchanged product rows previously rebuilt every tick. They now retain their controls; model expansion survives changed data as well.
- **New drill-down/export:** per-model totals follow the selected calendar window. CSV includes summary fields only, invariant numbers, escaped/literal model names and a UTF-8 BOM. The save uses a temporary file then replaces the destination. Export is disabled while a different period is being calculated.
- Added an independent reference comparison over 12,000 mixed events across all periods and DST, shared-session/model checks, quota reset boundaries, CSV escaping/culture checks and aggregation cancellation.
- Added actual WPF scenarios/assertions for model expansion, unchanged-row identity, expired quotas with unchanged logs, and export readiness during period changes. Existing narrow, maximized, restored, 1080p/2K/4K and settings checks remain in place.
- Local validation: **22 core tests passed**, WPF Release cross-compilation passed with zero warnings/errors, and the existing **3 Swift tests plus Swift Release build passed**. Windows runtime/render validation runs separately in the Windows workflow; physical mixed-DPI and interactive save-dialog testing still require Windows desktop access.

Follow-up candidates: incremental parsing of very large append-only files (requires preserving cumulative counter state across truncation/rotation); real Windows mixed-DPI and Explorer restart validation. No incremental-parser shortcut or WSL discovery is introduced in this revision.

## Windows UI redesign

UI source revision: `d673702`. [Windows CI](https://github.com/leeeway/TokenPulse/actions/runs/39356350336) and the independent [macOS CI](https://github.com/leeeway/TokenPulse/actions/runs/39356350330) both passed.

- Windows: 19 core tests, self-contained packaging, real WPF/tray startup and visual scenarios passed.
- Generated 18 PNGs: normal, warning, empty, large values, narrow/multiple providers, expanded source details, settings, and tray icons. Window screenshots were exported at 96 and 144 DPI.
- Reviewed the actual WPF renders for card layout, text, chart visibility, large-number scaling and compact settings. Representative screenshots are in `docs/`; the complete set is in the `Windows-UI-previews` CI artifact.
- The visual executable also exercised segmented period selection, hide-on-close and preference switch construction. All data and paths in these previews are synthetic.
- Mac source, package, tests, packaging script and workflow remain unchanged. Mac CI passed on the UI revision.

## Completed

- Existing tracked repository files remain byte-for-byte unchanged. Additions are confined to `windows/` and `.github/workflows/windows-ci.yml`.
- Existing Swift test baseline and post-change regression: **3 passed** in both runs.
- Existing Swift Release build: **passed** before and after the Windows additions.
- C# core suite: **19 passed, 0 failed** on macOS. Includes the copied Swift fixtures, independent expected costs, cumulative deltas, cache writes, malformed/partial JSONL, calendar boundaries, DST, quota-only records, repeated scans, append/truncate/delete/recreate, Unicode paths, exclusive access failure, settings recovery, cancellation and mocked Ollama behavior.
- The Claude, Codex and Antigravity fixtures exactly match the sample strings in the existing Swift tests; the original tests were not edited.
- WPF Release cross-compilation: **passed, 0 warnings, 0 errors**.
- Self-contained `win-x64` publish: **passed**. Includes .NET and Windows Desktop runtimes; no system-wide .NET installation is required on the target machine.
- Project XML, application manifest, settings JSON and JSONL fixture syntax: **valid**.
- PowerShell packaging and packaged `--smoke-test`: **passed on Windows CI**. The local ZIP was replaced with the verified CI artifact.

The Swift checks used temporary module cache directories and `--disable-sandbox` because this execution environment cannot write the normal compiler cache location. No repository build settings were changed.

## Still requires Windows / desktop access

- Windows 11 tray interactions, login startup, Explorer restart recovery, mixed-DPI monitors and sleep/resume require the manual checklist in README.md. Cross-compilation does not verify these behaviors.
- Mac status-bar clicks, refresh and time-window switching could not be automated because Computer Use permission is not granted. Compilation and tests passed; interactive regression remains unverified.

## Reproduce

From `windows/` with .NET 10 SDK:

```powershell
dotnet run --project TokenPulse.Tests/TokenPulse.Tests.csproj -c Release
dotnet build TokenPulse.Windows/TokenPulse.Windows.csproj -c Release
./scripts/build.ps1
./dist/TokenPulse-Windows-x64/TokenPulse.exe --smoke-test
```

The tests are a dependency-free executable, so use `dotnet run`, not `dotnet test`.
