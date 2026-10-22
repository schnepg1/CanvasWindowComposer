@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\Tools\RunCanvasTests.ps1" %*
exit /b %errorlevel%
