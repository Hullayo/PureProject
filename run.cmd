@echo off
if "%~1"=="" if exist "%~dp0artifacts\publish\win-x64-2.0.1\PureProject.exe" (
    start "" /D "%~dp0artifacts\publish\win-x64-2.0.1" "%~dp0artifacts\publish\win-x64-2.0.1\PureProject.exe"
    exit /b 0
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Task Run -Configuration Debug %*
if errorlevel 1 pause
