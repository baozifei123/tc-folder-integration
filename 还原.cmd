@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0TC-Manager.ps1" -Action Restore %*
set "result=%ERRORLEVEL%"
if not "%result%"=="0" echo Operation stopped. No success was reported.
echo.
pause
exit /b %result%
