@echo off
setlocal
cd /d "%~dp0.."

where python >nul 2>nul
if errorlevel 1 (
  echo [Vchem AI] Python not found in PATH.
  echo Install Python 3.10+ or add it to PATH.
  pause
  exit /b 1
)

echo [Vchem AI] starting http://127.0.0.1:8000
echo Close this window to stop the service.
python ai-service\app.py
pause
