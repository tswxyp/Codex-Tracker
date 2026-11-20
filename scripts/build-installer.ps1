param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version,
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory,
    [string]$PreviousPackage
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tool = $Dotnet
if ($tool -eq 'dotnet' -and !(dotnet --list-sdks)) {
    $tool = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuild/sdk/dotnet.exe'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts/release/v$Version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force)) {
    throw "Choose an empty output directory: $output"
}
$stage = Join-Path $repo ('artifacts/installer-build/' + [guid]::NewGuid())
$app = Join-Path $stage 'app'
$packages = Join-Path $stage 'packages'

if ($PreviousPackage) {
    $base = Get-Item -LiteralPath $PreviousPackage
    if ($base.Name -cnotmatch '^CodexTrackerDesktop-(\d+\.\d+\.\d+)-win-x64-full\.nupkg$' -or
        [version]$Matches[1] -ge [version]$Version) {
        throw 'Expected a full Windows x64 package from an earlier Codex Tracker release'
    }
    New-Item -ItemType Directory -Path $packages -Force | Out-Null
    Copy-Item -LiteralPath $base.FullName -Destination $packages
}

function Invoke-Dotnet([string[]]$Arguments) {
    & $tool @Arguments
    if ($LASTEXITCODE) { throw "dotnet $($Arguments[0]) failed ($LASTEXITCODE)" }
}

Push-Location $repo
try {
    Invoke-Dotnet @('tool','restore')
    Invoke-Dotnet @('restore','client/Tracker.Tray','--locked-mode','-r','win-x64','-p:SelfContained=true')
    foreach ($project in @('Tracker.Worker','Tracker.Tray')) {
        Invoke-Dotnet @('publish',"client/$project",'-c','Release','-r','win-x64','--self-contained','true','--no-restore',
            "-p:Version=$Version",'-p:DebugType=None','-o',$app)
    }
    Copy-Item -LiteralPath 'LICENSE','docs/third-party.md' -Destination $app
    Copy-Item -LiteralPath 'docs/licenses' -Destination $app -Recurse
    $assets = Get-Content 'client/Tracker.Tray/obj/project.assets.json' -Raw | ConvertFrom-Json
    $runtimePacks = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object name -in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')
    if (@($runtimePacks).Count -ne 2) { throw 'Missing runtime pack metadata' }
    foreach ($pack in $runtimePacks) {
        $packVersion = $pack.version.TrimStart('[').Split(',')[0]
        $relative = Join-Path $pack.name.ToLowerInvariant() $packVersion
        $folder = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $relative } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        $notices = Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$' }
        if (!$notices) { throw "Missing runtime notices for $relative" }
        foreach ($notice in $notices) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $app "licenses/$($pack.name)-$($notice.Name)")
        }
    }
    foreach ($file in @('CodexTracker.Tray.exe','CodexTracker.exe','coreclr.dll','System.Windows.Forms.dll','Velopack.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) { throw "Missing application file: $file" }
    }
    Invoke-Dotnet @('tool','run','vpk','--','pack','--packId','CodexTrackerDesktop','--packTitle','Codex Tracker',
        '--packAuthors','Codex Tracker contributors','--packVersion',$Version,'--packDir',$app,'--mainExe','CodexTracker.Tray.exe',
        '--icon','client/Tracker.Tray/Tracker.ico','--runtime','win-x64','--channel','win-x64','--shortcuts','StartMenuRoot',
        '--noPortable','--delta','BestSpeed','--outputDir',$packages)

    $feed = Get-Item -LiteralPath (Join-Path $packages 'releases.win-x64.json')
    $index = Get-Content $feed.FullName -Raw | ConvertFrom-Json
    # GithubSource resolves each feed entry against the attachments on that particular release.
    $index.Assets = @($index.Assets | Where-Object Version -CEQ $Version)
    $expectedTypes = @('Full')
    if ($PreviousPackage) { $expectedTypes += 'Delta' }
    if ($index.Assets.Count -ne $expectedTypes.Count) { throw 'Unexpected update feed' }
    $releasePackages = foreach ($type in $expectedTypes) {
        $name = "CodexTrackerDesktop-$Version-win-x64-" + $type.ToLowerInvariant() + '.nupkg'
        $entries = @($index.Assets | Where-Object { $_.Type -ceq $type -and $_.FileName -ceq $name })
        if ($entries.Count -ne 1) { throw "Missing update feed entry for $name" }
        $package = Get-Item -LiteralPath (Join-Path $packages $name)
        if ($package.Length -ne $entries[0].Size -or (Get-FileHash $package.FullName).Hash -ne $entries[0].SHA256) {
            throw "Update feed checksum mismatch for $name"
        }
        $package.FullName
    }
    $setup = @(Get-ChildItem -LiteralPath $packages -Filter '*-Setup.exe' -File)
    if ($setup.Count -ne 1) { throw 'Expected one Windows installer' }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Copy-Item -LiteralPath $setup[0].FullName -Destination (Join-Path $output 'CodexTracker-Setup-x64.exe')
    Copy-Item -LiteralPath $releasePackages -Destination $output
    $index | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath (Join-Path $output $feed.Name) -Encoding utf8
    $hashes = Get-ChildItem $output -File | Sort-Object Name | ForEach-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
    }
    $hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding Ascii
    Write-Output "Release files: $output"
} finally {
    Pop-Location
}
