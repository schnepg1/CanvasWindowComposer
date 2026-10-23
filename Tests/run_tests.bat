@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\Tools\Run-Tests.ps1" %*
exit /b %errorlevel%
