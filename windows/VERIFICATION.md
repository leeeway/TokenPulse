# Windows implementation verification

Status recorded on 2026-10-20. Host: macOS arm64. SDK used for C#: .NET 10.0.402 installed temporarily outside the repository.

## Completed

- Existing tracked repository files remain byte-for-byte unchanged. Additions are confined to `windows/` and `.github/workflows/windows-ci.yml`.
- Existing Swift test baseline and post-change regression: **3 passed** in both runs.
- Existing Swift Release build: **passed** before and after the Windows additions.
- C# core suite: **19 passed, 0 failed** on macOS. Includes the copied Swift fixtures, independent expected costs, cumulative deltas, cache writes, malformed/partial JSONL, calendar boundaries, DST, quota-only records, repeated scans, append/truncate/delete/recreate, Unicode paths, exclusive access failure, settings recovery, cancellation and mocked Ollama behavior.
- The Claude, Codex and Antigravity fixtures exactly match the sample strings in the existing Swift tests; the original tests were not edited.
- WPF Release cross-compilation: **passed, 0 warnings, 0 errors**.
- Self-contained `win-x64` publish: **passed**. Includes .NET and Windows Desktop runtimes; no system-wide .NET installation is required on the target machine.
- Project XML, application manifest, settings JSON and JSONL fixture syntax: **valid**.

The Swift checks used temporary module cache directories and `--disable-sandbox` because this execution environment cannot write the normal compiler cache location. No repository build settings were changed.

## Still requires Windows / desktop access

- The new GitHub Actions workflow is configured but has not been pushed or executed in this session.
- The PowerShell packaging script and packaged `--smoke-test` must run on Windows CI. The local ZIP was assembled from the successful publish output with Python because PowerShell is not installed on this Mac.
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
