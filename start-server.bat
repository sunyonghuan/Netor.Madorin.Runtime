@echo off
title Madorin Runtime Server (Meeting)
set MADORIN_HANDSHAKE_SECRET=test-secret-dev
cd /d "%~dp0"
echo Starting Madorin server from meeting-mode worktree...
echo Workspace: %CD%
dotnet run --project src/Madorin.AI.Runtime.Cli -- serve --workspace "%~dp0test-workspace"
pause
