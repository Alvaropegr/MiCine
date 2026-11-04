@echo off
title MiCine
cd /d "%~dp0"
where py >nul 2>nul
if %errorlevel%==0 (py -3 servidor.py %*) else (python servidor.py %*)
echo.
pause
