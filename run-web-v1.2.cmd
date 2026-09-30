@echo off
setlocal
cd /d "%~dp0"

set "APP_PORT=1420"
if not "%~1"=="" set "APP_PORT=%~1"

set "NODE_EXE=%~dp0runtime\node.exe"
set "VITE_ENTRY=%~dp0node_modules\vite\bin\vite.js"

if not exist "%NODE_EXE%" (
  echo [V1.2] Bundled Node.js runtime not found:
  echo %NODE_EXE%
  pause
  exit /b 1
)

if not exist "%VITE_ENTRY%" (
  echo [V1.2] Bundled dependencies are incomplete:
  echo %VITE_ENTRY%
  pause
  exit /b 1
)

echo [V1.2] Starting at http://127.0.0.1:%APP_PORT%/
echo [V1.2] Press Ctrl+C to stop.
"%NODE_EXE%" "%VITE_ENTRY%" dev --host 127.0.0.1 --port "%APP_PORT%" --strictPort
exit /b %ERRORLEVEL%
