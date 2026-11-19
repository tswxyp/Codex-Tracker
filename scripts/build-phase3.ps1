param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($Dotnet -eq 'dotnet' -and !(dotnet --list-sdks)) {
    $Dotnet = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
}
$repo = Split-Path -Parent $PSScriptRoot
foreach ($target in @(
    @{ Project = 'client/Tracker.Sync'; Output = 'windows-x64' },
    @{ Project = 'server/Tracker.Server'; Output = 'server' }
)) {
    $project = Join-Path $repo $target.Project
    $out = Join-Path $repo ('artifacts/phase3/' + $target.Output)
    & $Dotnet restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Locked dependency restore failed.' }
    & $Dotnet publish $project -c Release --no-restore --no-self-contained -p:DebugType=None -o $out
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $out
    Copy-Item -LiteralPath (Join-Path $repo 'docs/phase-3-run.md') -Destination $out
    Copy-Item -LiteralPath (Join-Path $repo 'docs/phase-3-dependencies.md') -Destination $out
    $licenses = Join-Path $out 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    Get-ChildItem -File -LiteralPath (Join-Path $repo 'docs/licenses/phase3') | Copy-Item -Destination $licenses -Force
    Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses/SQLitePCLRaw-Apache-2.0.txt') -Destination $licenses -Force
    if ($target.Output -eq 'windows-x64') {
        $archive = Join-Path $repo 'artifacts/phase3/CodexTracker-Sync-Windows-x64.zip'
        $files = @('Tracker.Sync.exe','Tracker.Sync.dll','Tracker.Sync.deps.json','Tracker.Sync.runtimeconfig.json',
            'Tracker.Protocol.dll','Microsoft.Data.Sqlite.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.winsqlite3.dll',
            'System.Security.Cryptography.ProtectedData.dll','LICENSE','phase-3-run.md','phase-3-dependencies.md','licenses')
        Compress-Archive -LiteralPath @($files | ForEach-Object { Join-Path $out $_ }) -DestinationPath $archive -Force
    }
    Write-Output $out
}
