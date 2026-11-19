param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($Dotnet -eq 'dotnet') {
    $privateSdk = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
    if ((Test-Path -LiteralPath $privateSdk) -and !(dotnet --list-sdks)) { $Dotnet = $privateSdk }
}
$repo = Split-Path -Parent $PSScriptRoot
$viewer = Join-Path $repo 'client/Tracker.Viewer/Tracker.Viewer.csproj'
$worker = Join-Path $repo 'client/Tracker.Worker/Tracker.Worker.csproj'
$out = Join-Path $repo 'artifacts/phase1/windows-x64'
& $Dotnet restore $viewer --locked-mode
if ($LASTEXITCODE) { throw 'NuGet restore failed.' }
foreach ($project in @($worker,$viewer)) {
    & $Dotnet publish $project -c Release --no-restore --no-self-contained -p:DebugType=None -o $out
    if ($LASTEXITCODE) { throw 'Publish failed.' }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'docs/third-party.md') -Destination $out
Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses') -Destination $out -Recurse -Force
Write-Output $out
