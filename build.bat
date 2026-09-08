@echo off
setlocal enabledelayedexpansion

if exist publish_output rd /s /q publish_output

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish_output -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
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
