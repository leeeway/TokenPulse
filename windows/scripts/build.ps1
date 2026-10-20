param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
if ($Runtime -ne "win-x64") { throw "This packaging script supports win-x64 only." }
$WindowsRoot = Split-Path $PSScriptRoot -Parent
$OutputDirectory = Join-Path $WindowsRoot "dist/TokenPulse-Windows-x64"
$Archive = Join-Path $WindowsRoot "dist/TokenPulse-Windows-x64.zip"
Push-Location $WindowsRoot
try {
    if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
    dotnet publish TokenPulse.Windows/TokenPulse.Windows.csproj -c Release -r $Runtime --self-contained true -p:PublishTrimmed=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    Copy-Item README.md (Join-Path $OutputDirectory "README.md")
    Copy-Item LICENSE (Join-Path $OutputDirectory "LICENSE.TokenPulse.txt")
    Copy-Item docs (Join-Path $OutputDirectory "docs") -Recurse
    $BuildVersion = dotnet msbuild TokenPulse.Windows/TokenPulse.Windows.csproj -getProperty:Version
    if ($LASTEXITCODE -ne 0) { throw "Could not read the application version" }
    $Commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { $Commit = "unknown" }
    "TokenPulse Windows $BuildVersion`nCommit: $Commit`nRuntime: $Runtime" | Set-Content (Join-Path $OutputDirectory "BUILD-INFO.txt") -Encoding utf8
    Compress-Archive -Path "$OutputDirectory/*" -DestinationPath $Archive -Force
    Write-Host "Created $Archive"
} finally { Pop-Location }
