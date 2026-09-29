@echo off
chcp 936 >nul
setlocal

rem ============================================================
rem  编译所有插件（契约程序集 + 各插件）：
rem    - 装进用户插件目录 %APPDATA%\MinecraftChatOverlay\plugins\
rem    - 同时在 %APPDATA%\MinecraftChatOverlay\plugin-packages\ 打出可分发 zip
rem
rem  用之前先把软件关掉：dll 被运行时锁住时覆盖会失败。
rem ============================================================

set HERE=%~dp0
set PLUGINS_ROOT=%APPDATA%\MinecraftChatOverlay\plugins
set PKG_ROOT=%APPDATA%\MinecraftChatOverlay\plugin-packages

echo [1/3] 编译契约程序集...
dotnet build "%HERE%Plugins\Abstractions\MinecraftChatOverlay.Plugin.Abstractions.csproj" -c Release
if errorlevel 1 goto fail

if not exist "%PLUGINS_ROOT%" mkdir "%PLUGINS_ROOT%"
if not exist "%PKG_ROOT%" mkdir "%PKG_ROOT%"

echo.
echo [2/3] 编译并安装插件...
call :build RegionMagnifier goldiamond.regionmagnifier RegionMagnifierPlugin.dll
if errorlevel 1 goto fail
call :build AutoGg goldiamond.autogg AutoGgPlugin.dll
call :build SamplePlugin goldiamond.sample SamplePlugin.dll
if errorlevel 1 goto fail

echo.
echo [3/3] 完成
echo   已装到：%PLUGINS_ROOT%
echo   插件包：%PKG_ROOT%\*.zip（可以拖进软件的「插件」页试装）
goto end

:build
rem %1 = 工程目录名   %2 = 插件 id   %3 = 主 dll 文件名
echo   - 编译 %1 ...
dotnet build "%HERE%Plugins\%1\%1.csproj" -c Release >nul
if errorlevel 1 echo    !! %1 编译失败 & exit /b 1
set SRC=%HERE%Plugins\%1\bin\Release\net8.0-windows
set DST=%PLUGINS_ROOT%\%2
if not exist "%DST%" mkdir "%DST%"
copy /y "%SRC%\%3" "%DST%\" >nul
if errorlevel 1 echo    !! 覆盖失败（软件是不是还开着？）& exit /b 1
copy /y "%HERE%Plugins\%1\plugin.json" "%DST%\" >nul
powershell -NoProfile -Command "Compress-Archive -Path '%DST%\*' -DestinationPath '%PKG_ROOT%\%2.zip' -Force" >nul
echo     装好 %2；包：%PKG_ROOT%\%2.zip
exit /b 0

:fail
echo.
echo !! 出错了，看上面的输出
exit /b 1

:end
endlocal
