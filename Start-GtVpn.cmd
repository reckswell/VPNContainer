@echo off
rem GT VPN in one go: double-click this. The first time it runs the setup;
rem then it starts Docker Desktop if needed, brings up the gtvpn container
rem and signs in with gtvpn-login.conf, so you type only the Duo code.
rem Options:  Start-GtVpn.cmd -SavePassword   Start-GtVpn.cmd -Check
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\Start-GtVpn.ps1" %*
set "rc=%errorlevel%"
echo.
pause
exit /b %rc%
