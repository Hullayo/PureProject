@echo off
setlocal
cd /d "%~dp0"

set "APP_EXE=%~dp0app\Jianxiang-V1.2.exe"

if not exist "%APP_EXE%" (
  echo [V1.2] Desktop executable not found:
  echo %APP_EXE%
  echo.
  echo Use run-web-v1.2.cmd to start the bundled web version.
  pause
  exit /b 1
)

start "" "%APP_EXE%"
exit /b 0
