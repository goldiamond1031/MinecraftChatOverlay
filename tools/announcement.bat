@echo off
chcp 936 >nul
rem 双击打开发公告的窗口
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0announcement-gui.ps1"