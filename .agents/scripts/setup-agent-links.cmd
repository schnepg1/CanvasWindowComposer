@echo off
setlocal
REM ============================================================================
REM Cross-Tool Windows Junction Setup Wrapper (.agents/scripts/setup-agent-links.cmd)
REM ============================================================================
REM Sets up NTFS directory junctions linking .gemini\skills and .opencode\skills
REM to .agents\skills so that Antigravity, OpenCode, and Codex tools seamlessly
REM share the same agent skills.
REM
REM Usage:
REM   setup-agent-links.cmd           (Create / Verify junctions)
REM   setup-agent-links.cmd -Check    (Check junction status)
REM   setup-agent-links.cmd -Remove   (Remove junctions)
REM   setup-agent-links.cmd -Force    (Overwrite non-junction directories)
REM ============================================================================

set SCRIPT_DIR=%~dp0
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%setup-agent-links.ps1" %*

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] setup-agent-links failed with exit code %ERRORLEVEL%.
    exit /b %ERRORLEVEL%
)

endlocal
exit /b 0
