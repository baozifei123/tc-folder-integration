@echo off
setlocal
if not exist "%~dp0TCWeChatRedirector.exe" (
    echo ERROR: TCWeChatRedirector.exe was not found next to this script.
    pause
    exit /b 1
)
start /wait "" "%~dp0TCWeChatRedirector.exe" --status
powershell -NoProfile -Command "Get-Content -LiteralPath '%~dp0redirector-status.txt'"
echo.
pause
