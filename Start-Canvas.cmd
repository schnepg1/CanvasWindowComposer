@echo off
setlocal
pushd "%~dp0"
if /I "%~1"=="--build" goto build
if exist "publish-workspace\CanvasDesktop.exe" goto launch
:build
dotnet publish CanvasDesktop.csproj -c Release -o publish-workspace
if errorlevel 1 (
    popd
    pause
    exit /b 1
)
:launch
start "" /d "%~dp0publish-workspace" "%~dp0publish-workspace\CanvasDesktop.exe"
popd
