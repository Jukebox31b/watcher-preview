@echo off
setlocal
set "WATCHER_EXE=%~dp0DcsWatcherV2.exe"
if not exist "%WATCHER_EXE%" set "WATCHER_EXE=%~dp0artifacts\WatcherPreview-win-x64\DcsWatcherV2.exe"

if not exist "%WATCHER_EXE%" (
    echo ERROR: Watcher Preview is not published.
    echo Run PUBLISH-WATCHER-PREVIEW.cmd first.
    exit /b 1
)

start "Watcher Preview - Demo UI" "%WATCHER_EXE%" --demo-ui
if errorlevel 1 (
    echo ERROR: Watcher Preview could not be launched.
    exit /b 1
)
exit /b 0
