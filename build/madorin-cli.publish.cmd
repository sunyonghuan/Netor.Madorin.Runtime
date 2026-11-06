@echo off
setlocal

rem Fixed version example: madorin-cli.publish.cmd win -Version 1.2.3
set "PUBLISH_SCRIPT=%~dp0madorin-cli.publish.ps1"
if not exist "%PUBLISH_SCRIPT%" (
    echo [ERROR] Publish script was not found: "%PUBLISH_SCRIPT%"
    exit /b 1
)

where pwsh.exe >nul 2>nul
if not errorlevel 1 (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" %*
) else (
    echo [WARN] pwsh.exe was not found, falling back to Windows PowerShell.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" %*
)

set "EXITCODE=%ERRORLEVEL%"
pause
exit /b %EXITCODE%
