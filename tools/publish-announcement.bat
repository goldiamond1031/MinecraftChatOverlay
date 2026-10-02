@echo off
chcp 936 >nul
rem 双击这个就行；参数原样转给 ps1，例如：
rem   publish-announcement.bat -Show
rem   publish-announcement.bat -Title "更新提示" -Body "更新了 1.2.8" -Link "https://github.com/goldiamond1031/MinecraftChatOverlay/releases" -LinkText "有可用更新"
rem   publish-announcement.bat -Disable
rem   publish-announcement.bat -Title x -Body y -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-announcement.ps1" %*
echo.
pause