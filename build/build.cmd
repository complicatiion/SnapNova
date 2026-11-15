@echo off
rem SnapNova release build: tests, self-contained publish, portable ZIP, Inno Setup installer, SHA-256 files.
rem Double-click or run from any folder (also from a network drive). Arguments are passed on, e.g.:
rem   build\build.cmd -SkipTests
rem   build\build.cmd -SkipInstaller
rem Uses PowerShell 7 (pwsh) when installed, otherwise Windows PowerShell 5.1 - always with -ExecutionPolicy Bypass.
setlocal
set "SCRIPT=%~dp0publish.ps1"
where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
)
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo. & echo Build failed with exit code %RESULT%.
rem Keep the window open when started by double-click.
echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %RESULT%
