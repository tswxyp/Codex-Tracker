param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($Dotnet -eq 'dotnet' -and !(dotnet --list-sdks)) {
    $Dotnet = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
}
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts/phase4'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = [IO.Directory]::CreateDirectory([IO.Path]::Combine($tempRoot, 'tracker-p4-' + [guid]::NewGuid())).FullName
function Publish-Tracker($project, $directory) {
    $path = Join-Path $repo $project
    & $Dotnet restore $path --locked-mode
    if ($LASTEXITCODE) { throw "Locked restore failed for $project" }
    & $Dotnet publish $path -c Release --no-restore --no-self-contained -p:DebugType=None -o $directory
    if ($LASTEXITCODE) { throw "Publish failed for $project" }
}
try {
    $windows = Join-Path $stage 'windows'
    $server = Join-Path $stage 'server'
    Publish-Tracker 'client/Tracker.Worker' (Join-Path $windows 'tray')
    Publish-Tracker 'client/Tracker.Tray' (Join-Path $windows 'tray')
    Publish-Tracker 'client/Tracker.Sync' (Join-Path $windows 'sync')
    Publish-Tracker 'server/Tracker.Server' $server
    foreach ($directory in @($windows,$server)) {
        foreach ($name in @('LICENSE','docs/phase-4-run.md','docs/phase-3-run.md','docs/phase-3-dependencies.md','docs/third-party.md')) {
            Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $directory
        }
        Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses') -Destination $directory -Recurse
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in @(@{Root=$windows;Name='CodexTracker-Phase4-Windows-x64.zip'},@{Root=$server;Name='CodexTracker-Phase4-Server.zip'})) {
        $zip = Join-Path $stage $package.Name
        [IO.Compression.ZipFile]::CreateFromDirectory($package.Root,$zip)
        Copy-Item -LiteralPath $zip -Destination (Join-Path $release $package.Name) -Force
        Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object -ExpandProperty Hash
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot.TrimEnd('\','/') -and [IO.Path]::GetFileName($resolved) -match '^tracker-p4-[a-f0-9-]{36}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
