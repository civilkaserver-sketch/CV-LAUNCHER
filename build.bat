@echo off
cd /d "%~dp0"
echo ========================================================
echo   Compiling CV Launcher (Stand-alone Build)
echo ========================================================
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:"CV Launcher.exe" /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:ui.html,ui.html /resource:manifest.json,manifest.json /resource:news.json,news.json /resource:icon.ico,icon.ico /resource:assets\icon.png,assets/icon.png /resource:assets\minecraft-rus.ttf,assets/minecraft-rus.ttf /resource:assets\news_banner_1.svg,assets/news_banner_1.svg /resource:assets\news_banner_1.png,assets/news_banner_1.png /resource:assets\bg.png,assets/bg.png Launcher.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCCESS] "CV Launcher.exe" built successfully!
) else (
    echo.
    echo [ERROR] Compilation failed! Make sure CV Launcher.exe is closed.
)
pause
