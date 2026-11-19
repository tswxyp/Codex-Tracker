param([string]$Dotnet = 'dotnet', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($Dotnet -eq 'dotnet' -and !(dotnet --list-sdks)) {
    $Dotnet = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
}
$repo = Split-Path -Parent $PSScriptRoot
$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'artifacts/phase2/windows-x64' }
foreach ($name in @('Worker','Tray')) {
    $project = Join-Path $repo "client/Tracker.$name/Tracker.$name.csproj"
    & $Dotnet restore $project --locked-mode
    if ($LASTEXITCODE) { throw 'Restore failed' }
    & $Dotnet publish $project -c Release --no-restore --no-self-contained -p:DebugType=None -o $out
    if ($LASTEXITCODE) { throw 'Publish failed' }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'docs/third-party.md') -Destination $out
$licenseOut = Join-Path $out 'licenses'
New-Item -ItemType Directory -Force -Path $licenseOut | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repo 'docs/licenses') -File | Copy-Item -Destination $licenseOut
Write-Output $out
