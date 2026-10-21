@echo off
setlocal
set "canvas_args=%*"
rem Empty variables must bypass cmd's argument substitutions.
if not defined canvas_args goto launch
set "canvas_args=%canvas_args:--build=-Build%"
set "canvas_args=%canvas_args:--diagnose=-Diagnose%"
set "canvas_args=%canvas_args:--no-launch=-NoLaunch%"
set "canvas_args=%canvas_args:--no-dialog=-NoDialog%"
:launch
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Start-Canvas.ps1" %canvas_args%
set "canvas_exit=%errorlevel%"
if not "%canvas_exit%"=="0" (
    echo Canvas did not start. Error details are in the startup log and the copyable dialog.
    if not defined CANVAS_NO_PAUSE pause
)
exit /b %canvas_exit%
