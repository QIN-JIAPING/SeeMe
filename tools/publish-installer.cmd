@echo off
REM SeeMe release script: version is read automatically from <Version> in SeeMe.csproj
REM (single source of truth) and injected into ZIP / banner bitmap / NSIS installer.
REM Requires: NSIS at D:\allll\NSIS
REM NOTE: keep this file ASCII-only with CRLF line endings so cmd parses it reliably
REM on any codepage (Chinese text belongs in .nsi/.ps1, not in .cmd).
setlocal
set DOTNET_ROOT=D:\allll\dotnet-sdk-8

REM ---- 1) Read version from csproj (single source) ----
powershell -NoProfile -Command "[regex]::Match((Get-Content 'D:\SeeMe\SeeMe.csproj' -Raw), '<Version>([^<]+)</Version>').Groups[1].Value" > "%TEMP%\seeme_version.txt"
set /p VERSION=<"%TEMP%\seeme_version.txt"
if "%VERSION%"=="" set VERSION=1.0.1
del "%TEMP%\seeme_version.txt" 2>nul

echo.
echo ===== SeeMe v%VERSION% release =====

echo.
echo === 1/4: Build ===
"D:\allll\dotnet-sdk-8\dotnet.exe" build -c Release
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo.
echo === 2/4: Portable ZIP ===
powershell -NoProfile -Command "Compress-Archive -Path 'D:\SeeMe\bin\Release\net8.0-windows\*' -DestinationPath 'D:\allll\SeeMe-v%VERSION%.zip' -Force"

echo.
echo === 3/4: Banner version ===
REM Prefer Python+Pillow full regeneration (make_installer_assets.py reads csproj itself);
REM fall back to in-place PowerShell patch (patch_version_bmp.ps1 -Version %VERSION%)
set "PY="
where python >nul 2>nul && set "PY=python"
if not defined PY where py >nul 2>nul && set "PY=py -3"
if defined PY (
  %PY% "%~dp0make_installer_assets.py"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch_version_bmp.ps1" -Version %VERSION%
)

REM 3.5) Keep installer-preview.html (design mockup) version strings in sync
powershell -NoProfile -Command "$p='%~dp0installer-preview.html'; if(Test-Path $p){ $c=[System.IO.File]::ReadAllText($p); $c=$c -replace 'v1\.0\.\d+','v%VERSION%' -replace 'SeeMe-Setup-1\.0\.\d+\.exe','SeeMe-Setup-%VERSION%.exe' -replace '1\.0\.\d+\.0','%VERSION%.0'; [System.IO.File]::WriteAllText($p,$c) }"

echo.
echo === 4/4: NSIS installer ===
REM NSI contains Chinese; must stay UTF-8 WITH BOM (makensis detects encoding via BOM)
powershell -NoProfile -Command "$p='%~dp0SeeMe-installer.nsi'; $b=[System.IO.File]::ReadAllBytes($p); if(-not($b[0]-eq 0xEF -and $b[1]-eq 0xBB -and $b[2]-eq 0xBF)){ $c=[System.IO.File]::ReadAllText($p); [System.IO.File]::WriteAllText($p, [char]0xFEFF + $c, (New-Object System.Text.UTF8Encoding $false)) }"
"D:\allll\NSIS\makensis.exe" /DAPP_VERSION=%VERSION% "%~dp0SeeMe-installer.nsi"

echo.
echo ===== Done =====
echo Installer: D:\allll\SeeMe-Setup-%VERSION%.exe
echo ZIP:       D:\allll\SeeMe-v%VERSION%.zip
endlocal
