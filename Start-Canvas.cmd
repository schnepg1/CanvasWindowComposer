@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Start-Canvas.ps1" %*
set "canvas_exit=%errorlevel%"
if not "%canvas_exit%"=="0" (
    echo Canvas did not start. Error details are in the startup log and the copyable dialog.
    if not defined CANVAS_NO_PAUSE pause
)
exit /b %canvas_exit%
