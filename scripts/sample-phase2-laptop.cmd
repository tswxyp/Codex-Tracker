@echo off
setlocal EnableExtensions DisableDelayedExpansion
pushd "%~dp0" || exit /b 1
if not exist "Tracker.Tray.Tests.exe" (
    echo Run this sampler from the extracted Phase 2 test kit.
    goto :failed
)
if not exist "evidence\" mkdir "evidence"
if not exist "evidence\" goto :failed
set "sampleId="
for /f %%G in ('powershell.exe -NoProfile -Command "[guid]::NewGuid().ToString()"') do set "sampleId=%%G"
if not defined sampleId goto :failed
set "sampleFile=evidence\%sampleId%.txt"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $os=Get-CimInstance Win32_OperatingSystem; $v=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'; $t=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'; $p=Get-AppxPackage -Name OpenAI.Codex; [ordered]@{collected=(Get-Date).ToString('o');build=$os.BuildNumber;revision=$v.UBR;release=$v.DisplayVersion;codex=[string]$p.Version;systemLight=$t.SystemUsesLightTheme;appsLight=$t.AppsUseLightTheme;trayDll=(Get-FileHash -Algorithm SHA256 -LiteralPath 'CodexTracker.Tray.dll').Hash} | ConvertTo-Json -Compress" > "%sampleFile%"
if errorlevel 1 goto :failed
echo Bring Codex to the front now. Leave its sidebar open for 8 seconds.
powershell.exe -NoProfile -Command "Start-Sleep -Seconds 8"
"Tracker.Tray.Tests.exe" live >> "%sampleFile%" 2>nul
if errorlevel 1 goto :failed
findstr /c:"PassThrough" "%sampleFile%" >nul || goto :failed
echo Captured. Review the counts and add your observations to %sampleFile%.
echo A captured sample still needs review against the test case.
popd
pause
exit /b 0
:failed
echo Sampling incomplete. Check the desktop runtime, foreground Codex, sidebar and tray.
if defined sampleFile echo Partial evidence file: %sampleFile%
popd
pause
exit /b 1
