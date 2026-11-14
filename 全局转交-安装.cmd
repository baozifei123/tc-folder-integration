@echo off
setlocal
if not exist "%~dp0TCWeChatRedirector.exe" (
    echo ERROR: TCWeChatRedirector.exe was not found next to this script.
    pause
    exit /b 1
)
start /wait "" "%~dp0TCWeChatRedirector.exe" --install
echo.
if not exist "%~dp0redirector-status.txt" (
    echo WARNING: Could not read redirector-status.txt.
    echo Check the sign-in entry and the redirector process manually.
    pause
    exit /b 1
)
findstr /C:"warning=previous_instance_still_running" "%~dp0redirector-status.txt" >nul 2>&1
if not errorlevel 1 (
    echo WARNING: A previous redirector instance is still running.
    echo Use its tray icon to exit, then run this again.
    echo.
    pause
    exit /b 2
)
findstr /C:"running=True" "%~dp0redirector-status.txt" >nul 2>&1
if not errorlevel 1 (
    echo Installed. All new Explorer windows will open in Total Commander.
    echo The redirector starts at sign-in for the current user.
) else (
    echo WARNING: The startup entry was written, but the redirector is not running.
    echo Check redirector-status.txt and explorer-redirect-log.txt.
)
echo.
pause
