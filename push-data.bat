@echo off
chcp 936 >nul
setlocal

rem ============================================================
rem  开发目录的数据  ->  公开分发仓（C:/Github/MinecraftChatOverlay）
rem    只搬这几样，源码一个字都不推：
rem      README.md                      仓库门面
rem      about\about.json               打赏榜
rem      about\announcement.json        开屏公告
rem      market\index.json              插件市场清单
rem      market\packages\*.zip          插件包
rem      Plugins\开发文档.html          插件开发文档（第三方作者要看）
rem   推完会 purge jsDelivr 缓存，用户立刻能拿到新的。
rem ============================================================

set "DEV=C:/MinecraftChatOverlayDSUI3"
set "REPO=C:/Github/MinecraftChatOverlay"
set "MSG=%~1"
if "%MSG%"=="" set "MSG=数据更新（榜单 / 公告 / 市场清单）"

echo [1/4] 复制数据文件...
copy /y "%DEV%\README.md"                        "%REPO%\README.md" >nul
copy /y "%DEV%\about\about.json"                 "%REPO%\about\about.json" >nul
copy /y "%DEV%\about\announcement.json"          "%REPO%\about\announcement.json" >nul
copy /y "%DEV%\market\index.json"                "%REPO%\market\index.json" >nul
copy /y "%DEV%\Plugins\开发文档.html"            "%REPO%\Plugins\开发文档.html" >nul
robocopy "%DEV%\market\packages" "%REPO%\market\packages" /E /NDL /NFL /NJH /NP >nul
if errorlevel 8 (
    echo !! 复制插件包出错，停下
    goto :end
)

echo [2/4] 提交...
git -C "%REPO%" add -A
git -C "%REPO%" diff --cached --quiet
if not errorlevel 1 (
    echo   没有改动，跳过提交
    goto :purge
) else (
    git -C "%REPO%" commit -m "%MSG%"
    if errorlevel 1 (
        echo !! 提交失败
        goto :end
    )
)

echo [3/4] 推送...
git -C "%REPO%" -c http.sslVerify=false push origin main
if errorlevel 1 (
    echo !! 推送失败，看上面的报错
    goto :end
)

:purge
echo [4/4] 刷新 jsDelivr 缓存...
set "PURGE_BASE=https://purge.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main"
curl -s -m 25 "%PURGE_BASE%/about/about.json" >nul 2>&1
curl -s -m 25 "%PURGE_BASE%/about/announcement.json" >nul 2>&1
curl -s -m 25 "%PURGE_BASE%/market/index.json" >nul 2>&1
echo   已请求刷新（返回 throttled 就是被限流，最多等 12 小时自然过期）

echo.
git -C "%REPO%" --no-pager log --oneline -1
echo   数据已推送：https://github.com/goldiamond1031/MinecraftChatOverlay
echo.

:end
endlocal
