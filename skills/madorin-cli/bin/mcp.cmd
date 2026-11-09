@echo off
setlocal
set "BRIDGE=%~dp0..\mcp\bin\mcp-bridge.mjs"
if not exist "%BRIDGE%" (
  echo Error: bridge not found at "%BRIDGE%" 1>&2
  exit /b 2
)
node "%BRIDGE%" %*
exit /b %ERRORLEVEL%
