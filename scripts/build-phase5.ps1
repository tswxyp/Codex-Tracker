param([string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
if ($Dotnet -eq 'dotnet' -and !(dotnet --list-sdks)) {
    $Dotnet=Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
}
$repo=Split-Path -Parent $PSScriptRoot
$release=Join-Path $repo 'artifacts/phase5'
$project=Join-Path $repo 'server/Tracker.Server'
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage=[IO.Directory]::CreateDirectory([IO.Path]::Combine($tempRoot,'tracker-p5-'+[guid]::NewGuid())).FullName
try {
    $publish=Join-Path $stage 'server'
    & $Dotnet restore $project --locked-mode
    if($LASTEXITCODE){throw 'Restore failed.'}
    & $Dotnet publish $project -c Release --no-restore --no-self-contained -p:DebugType=None -o $publish
    if($LASTEXITCODE){throw 'Publish failed.'}
    foreach($doc in @('LICENSE','docs/phase-5-run.md','docs/phase-4-run.md','docs/phase-3-run.md','docs/phase-3-dependencies.md','docs/third-party.md')){
        Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination $publish
    }
    Copy-Item -LiteralPath (Join-Path $repo 'docs/licenses') -Destination $publish -Recurse
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=Join-Path $stage 'CodexTracker-Phase5-Server.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip)
    New-Item -ItemType Directory -Force -Path $release | Out-Null
    Copy-Item -LiteralPath $zip -Destination $release -Force
    Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object -ExpandProperty Hash
} finally {
    $resolved=[IO.Path]::GetFullPath($stage)
    if([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot.TrimEnd('\','/') -and [IO.Path]::GetFileName($resolved) -match '^tracker-p5-[a-f0-9-]{36}$'){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
