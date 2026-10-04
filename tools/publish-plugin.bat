@echo off
chcp 936 >nul
setlocal
title 发布插件到市场 - MinecraftChatOverlay

rem ============================================================
rem  一条命令把一个插件发布到插件市场：
rem    1) 编译 Plugins\<插件名>
rem    2) 打包 market\packages\<插件id>-<版本>.zip（并删掉这个插件的旧版本包）
rem    3) 重建 market\index.json
rem
rem  两种用法：
rem    双击本文件           -> 会列出可用的插件，让你输入名字
rem    命令行带参数         -> publish-plugin.bat AutoGg
rem
rem  注意：本脚本故意不把插件装到你本机（这样才看得到市场里的「可更新」）。
rem  跑完下一步：push.bat
rem ============================================================

set "HERE=%~dp0"
set "ROOT=%HERE%.."
set "NAME=%~1"

if not "%NAME%"=="" goto :haveName

echo.
echo 可用的插件（Plugins\ 下的插件工程）：
for /d %%D in ("%ROOT%\Plugins\*") do (
    if exist "%%D\%%~nxD.csproj" echo     %%~nxD
)
echo.
set "NAME="
set /p "NAME=请输入要发布的插件名（上面的名字之一），然后回车；直接回车则取消："
set "NAME=%NAME:"=%"
if "%NAME%"=="" (
    echo 已取消。
    goto :end
)

:haveName
if not exist "%ROOT%\Plugins\%NAME%" (
    echo [错误] 找不到插件目录：Plugins\%NAME%
    goto :end
)
if not exist "%ROOT%\Plugins\%NAME%\%NAME%.csproj" (
    echo [错误] 找不到工程文件：Plugins\%NAME%\%NAME%.csproj
    echo        注意这里要填的是**插件目录名**（Plugins\ 下面的那一层）
    goto :end
)

echo.
echo ================ 1/3  编译 Plugins\%NAME% ================
dotnet build "%ROOT%\Plugins\%NAME%\%NAME%.csproj" -c Release
if errorlevel 1 (
    echo.
    echo [错误] 编译失败，先修好再发布。
    goto :end
)

echo.
echo ================ 2/3  打包市场包 ================
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%pack-market-package.ps1" -PluginDir "%ROOT%\Plugins\%NAME%" -PluginName "%NAME%"
if errorlevel 1 (
    echo.
    echo [错误] 打包失败。
    goto :end
)

echo.
echo ================ 3/3  重建市场清单 ================
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%rebuild-market-index.ps1"
if errorlevel 1 (
    echo.
    echo [错误] 重建清单失败。
    goto :end
)

echo.
echo   完成。下一步：push.bat 把 market 和插件源码推上去 ——
echo           它会顺手刷新 jsDelivr 缓存，用户那边立刻就能看到新版本。
echo.

:end
echo.
pause