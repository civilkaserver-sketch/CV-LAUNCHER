@echo off
cd /d "%~dp0"
echo ========================================================
echo   Compiling CV Launcher (Stand-alone Build with all Assets)
echo ========================================================
python build_launcher.py
pause
