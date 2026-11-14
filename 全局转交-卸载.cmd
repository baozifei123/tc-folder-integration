@echo off
setlocal
if not exist "%~dp0TCWeChatRedirector.exe" (
    reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v TCWeChatRedirector /f >nul 2>&1
    echo Removed the sign-in entry. The program file was missing.
    echo If a redirector process is still running, sign out to stop it.
    pause
    exit /b 0
)
start /wait "" "%~dp0TCWeChatRedirector.exe" --uninstall
echo.
if not exist "%~dp0redirector-status.txt" (
    echo WARNING: Could not read redirector-status.txt.
    echo Check the sign-in entry and the redirector process manually.
    pause
    exit /b 1
)
findstr /C:"running=True" "%~dp0redirector-status.txt" >nul 2>&1
if not errorlevel 1 (
    echo WARNING: The sign-in entry was removed, but the redirector is still running.
    echo Use its tray icon to exit or sign out, then run this again.
) else (
    echo Removed. The redirector was stopped and its sign-in entry was removed.
)
echo.
pause
