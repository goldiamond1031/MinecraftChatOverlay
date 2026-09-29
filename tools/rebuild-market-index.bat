@echo off
chcp 936 >nul
rem ============================================================
rem  重建插件市场清单 market\index.json
rem
rem  用法：
rem    rebuild-market-index.bat              只扫本地 market\packages\
rem    rebuild-market-index.bat -FromRepo    从 GitHub 仓库扫（网页上传的 zip 用这个）
rem    rebuild-market-index.bat -FromRepo -SyncPackages
rem                                          扫仓库 + 顺带把 zip 存回本地
rem
rem  加插件流程：zip 放进 market\packages\ → 跑这个 → push.bat
rem ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0rebuild-market-index.ps1" %*
pause