@echo off
cd /d "%~dp0\.."
echo ========================================================
echo   Compiling CV Launcher Beta (Stand-alone Clean Build)
echo ========================================================
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:"CV Launcher Beta.exe" /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:beta\ui.html,ui.html /resource:beta\manifest.json,manifest.json /resource:beta\news.json,news.json /resource:icon.ico,icon.ico /resource:assets\icon.png,assets/icon.png /resource:assets\minecraft-rus.ttf,assets/minecraft-rus.ttf /resource:assets\bg.png,assets/bg.png beta\Launcher.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] "CV Launcher Beta.exe" built successfully!
) else (
    echo.
    echo [ERROR] Compilation failed!
)
pause
