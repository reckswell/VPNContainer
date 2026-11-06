@echo off
rem GT VPN in one go: double-click this. The first time it runs the setup;
rem then it starts Docker Desktop if needed, brings up the vpncontainer container
rem and signs in with vpncontainer-login.conf, so you type only the Duo code.
rem Options:  Start-VpnContainer.cmd -SavePassword   Start-VpnContainer.cmd -Check
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\Start-VpnContainer.ps1" %*
set "rc=%errorlevel%"
echo.
pause
exit /b %rc%
