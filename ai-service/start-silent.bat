@echo off
setlocal
cd /d "%~dp0.."

where pythonw >nul 2>nul
if errorlevel 1 (
  echo [Vchem AI] pythonw not found in PATH; fall back to python.
  start "" /min python ai-service\app.py
  exit /b 0
)

start "" pythonw ai-service\app.py
exit /b 0
