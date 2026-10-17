@echo off
setlocal
if "%~1"=="" (
    echo Usage: Install\build-installer.bat 1.13.0-preview.1 preview
    echo Builds a self-contained installer with the pinned Velopack tool. NSIS is no longer required.
    exit /b 1
)
set "canvas_channel=%~2"
if "%canvas_channel%"=="" set "canvas_channel=preview"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\Tools\Deployment\Build-Release.ps1" -Version "%~1" -Channel "%canvas_channel%"
exit /b %errorlevel%
