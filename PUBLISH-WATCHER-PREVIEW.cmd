@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0PUBLISH-WATCHER-PREVIEW.ps1" %*
exit /b %ERRORLEVEL%
