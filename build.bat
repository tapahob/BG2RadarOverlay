@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0" || exit /b 1

rem A running copy locks its executable. Check it before removing the rest of the release.
if exist "publish_output\BG Radar Overlay.exe" del /q "publish_output\BG Radar Overlay.exe"
if exist "publish_output\BG Radar Overlay.exe" (
    echo Cannot replace BG Radar Overlay.exe. Close the copy running from publish_output and retry.
    exit /b 1
)
if exist publish_output rd /s /q publish_output
if exist publish_output exit /b 1

dotnet publish WPFFrontend\WPFFrontend.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish_output -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
if errorlevel 1 exit /b %errorlevel%

del /q "publish_output\*.pdb" 2>nul

for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item 'publish_output\BG Radar Overlay.exe').VersionInfo.FileVersion"`) do set VERSION=%%v

set SEVENZIP="C:\Program Files\7-Zip\7z.exe"
if not exist %SEVENZIP% (
    set SEVENZIP=
    for /f "usebackq delims=" %%p in (`where 7z 2^>nul`) do set SEVENZIP="%%p"
)

if not defined SEVENZIP (
    echo 7-Zip not found - skipping archive step.
) else (
    set ARCHIVE=publish_output\BG Radar Overlay %VERSION%.7z
    if exist "!ARCHIVE!" del "!ARCHIVE!"
    %SEVENZIP% a "!ARCHIVE!" ".\publish_output\*"
)

endlocal
