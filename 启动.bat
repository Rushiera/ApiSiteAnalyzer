@echo off
rem ==========================================================
rem  ApiSiteAnalyzer launcher
rem
rem  Why two slots: a running exe locks its own file, so a new
rem  build cannot be published into the slot that is running.
rem  Publish into the idle slot instead, then run this bat --
rem  it stops the old panel and starts the newest slot.
rem
rem  The browser instance is NOT killed here: it holds the login
rem  state and is re-adopted by the new panel on startup.
rem
rem  Keep this file ASCII-only, CRLF, no BOM.
rem ==========================================================
setlocal
cd /d "%~dp0"
set PORT=8766

set EXE=
for /f "delims=" %%f in ('dir /b /o-d /a-d "ApiSiteAnalyzer_*.exe" 2^>nul') do if not defined EXE set "EXE=%%f"
if not defined EXE (
  echo [ERROR] no ApiSiteAnalyzer_*.exe found in this folder
  pause
  exit /b 2
)

echo Stopping the panel listening on port %PORT% ...
set KILLED=0
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":%PORT% " ^| findstr "LISTENING"') do (
  echo   kill PID %%p
  taskkill /PID %%p /F >nul 2>nul
  set KILLED=1
)
if "%KILLED%"=="0" echo   (nothing was listening)
timeout /t 1 /nobreak >nul

echo Starting %EXE% ...
start "" "%EXE%" serve
