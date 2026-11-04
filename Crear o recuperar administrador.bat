@echo off
title MiCine - Administrador
cd /d "%~dp0"
where py >nul 2>nul
if %errorlevel%==0 (py -3 servidor.py --admin) else (python servidor.py --admin)
echo.
pause
