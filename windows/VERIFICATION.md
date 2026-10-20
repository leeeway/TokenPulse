# Windows implementation verification

Status recorded on 2026-10-20. Host: macOS arm64. SDK used for C#: .NET 10.0.402 installed temporarily outside the repository.

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
