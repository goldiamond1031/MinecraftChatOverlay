# ============================================================================
#  native/build.ps1 —— 用 MinGW-w64 编译原生部分（不需要 Visual Studio）
#
#  产物：
#    native\dist\GameMotionBlurHook.dll  注入进 Minecraft Java 版的钩子（OpenGL）
#    native\dist\TestGL.exe              验证 OpenGL 路径的测试画面（3.2 core profile）
# ============================================================================
[CmdletBinding()]
param(
    [string]$Gxx = "C:\mingw64\bin\g++.exe",
    [switch]$TestAppOnly,
    [switch]$HookOnly,
    [string]$OutName = "GameMotionBlurHook.dll"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $here "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not (Test-Path $Gxx)) {
    throw "找不到 g++：$Gxx，请用 -Gxx 指定 MinGW 的 g++.exe"
}

$defs = @(
    "-DUNICODE", "-D_UNICODE",
    "-DWIN32_LEAN_AND_MEAN",
    "-D_WIN32_WINNT=0x0A00", "-DNTDDI_VERSION=0x0A000000", "-DWINVER=0x0A00"
)
$flags = @(
    "-O2", "-std=c++17", "-fno-exceptions", "-fno-rtti",
    "-static-libgcc", "-static-libstdc++",
    "-Wall", "-Wno-unknown-pragmas", "-Wno-cast-function-type"
)
$libs = @(
    "-lopengl32", "-luser32", "-lgdi32", "-lole32", "-lpsapi", "-ladvapi32"
)

function Invoke-Native {
    param([string]$Name, [string[]]$Cmd)
    Write-Host ""
    Write-Host ">>> $Name" -ForegroundColor Cyan
    $exe = $Cmd[0]
    $rest = @()
    if ($Cmd.Count -gt 1) { $rest = $Cmd[1..($Cmd.Count - 1)] }
    Write-Host ($exe + " " + ($rest -join " ")) -ForegroundColor DarkGray
    & $exe $rest
    if ($LASTEXITCODE -ne 0) {
        throw "$Name 编译失败（exit $LASTEXITCODE）"
    }
}

if (-not $TestAppOnly) {
    $src = Join-Path $here "GameMotionBlur"
    $out = Join-Path $dist $OutName
    $cmd = @($Gxx, "-shared") + $flags + $defs + @(
        (Join-Path $src "dllmain.cpp"),
        (Join-Path $src "hook.cpp"),
        (Join-Path $src "gl_hook.cpp"),
        (Join-Path $src "gl_blur.cpp"),
        (Join-Path $src "bmp.cpp"),
        (Join-Path $src "control.cpp"),
        (Join-Path $src "log.cpp"),
        "-o", $out
    ) + $libs
    Invoke-Native "GameMotionBlurHook.dll" $cmd
}

if (-not $HookOnly) {
    $appSrc = Join-Path $here "TestApp"
    $bmpSrc = Join-Path $here "GameMotionBlur\bmp.cpp"

    $glTest = Join-Path $appSrc "TestGL.cpp"
    if (Test-Path $glTest) {
        $cmd = @($Gxx) + $flags + $defs + @(
            $glTest, $bmpSrc,
            "-I", (Join-Path $here "GameMotionBlur"),
            "-o", (Join-Path $dist "TestGL.exe")
        ) + $libs
        Invoke-Native "TestGL.exe" $cmd
    }
}

Write-Host ""
Write-Host "完成，产物在 $dist" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
