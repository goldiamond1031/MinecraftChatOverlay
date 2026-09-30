<#
.SYNOPSIS
    把某个插件打包成插件市场用的 zip（market\packages\<id>-<版本>.zip）。

.DESCRIPTION
    1. 读 Plugins\<插件>\plugin.json（版本号以它为准）
    2. 找出插件主 dll（plugin.json 的 assembly 字段，没有就自动挑）
    3. 打成 zip：条目名是 <id>/<dll> 和 <id>/plugin.json（正斜杠，标准 zip）
    4. 删掉 packages\ 里同一个 id 的其它版本包（保证一个插件只留最新那个）

    一般不用直接跑它 —— 用 tools\publish-plugin.bat <插件名> 一条命令走完全程。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PluginDir,
    [Parameter(Mandatory = $true)][string]$PluginName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$packagesDir = Join-Path $root 'market\packages'
if (-not (Test-Path -LiteralPath $packagesDir)) { New-Item -ItemType Directory -Path $packagesDir | Out-Null }

$manifestPath = Join-Path $PluginDir 'plugin.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "找不到 $manifestPath" }

$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$id = "$($manifest.id)"
$version = "$($manifest.version)"
if ([string]::IsNullOrWhiteSpace($id)) { throw 'plugin.json 里没有 id' }
if ([string]::IsNullOrWhiteSpace($version)) { throw 'plugin.json 里没有 version' }

# ---- 找主 dll ----
# 输出目录按 TFM 子目录自动找，别写死 net8.0-windows：
# 有的插件需要 WinRT 投影，TFM 会是 net8.0-windows10.0.19041.0。
$releaseRoot = Join-Path $PluginDir 'bin\Release'
$outDir = $null
if (Test-Path -LiteralPath $releaseRoot) {
    $outDir = Get-ChildItem -LiteralPath $releaseRoot -Directory |
        Where-Object { $_.Name -like 'net8.0-windows*' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}
if (-not $outDir) { throw "没有编译产物：$releaseRoot\net8.0-windows*（先编译插件）" }

$dllName = "$($manifest.assembly)"
if ([string]::IsNullOrWhiteSpace($dllName)) {
    $candidates = @(Get-ChildItem -LiteralPath $outDir -Filter '*.dll' | Where-Object { $_.Name -ne 'MinecraftChatOverlay.Plugin.Abstractions.dll' })
    $preferred = $candidates | Where-Object { $_.BaseName -eq "${PluginName}Plugin" } | Select-Object -First 1
    if ($preferred) { $dllName = $preferred.Name }
    elseif ($candidates.Count -eq 1) { $dllName = $candidates[0].Name }
    else {
        $byName = $candidates | Where-Object { $_.BaseName -like "${PluginName}*" } | Select-Object -First 1
        if ($byName) { $dllName = $byName.Name }
        else { throw "认不出主 dll（候选：$($candidates.Name -join ', ')），请在 plugin.json 里写 assembly 字段" }
    }
}
$dllPath = Join-Path $outDir $dllName
if (-not (Test-Path -LiteralPath $dllPath)) { throw "找不到 dll：$dllPath" }

# ---- 附属 dll ----
# 有的插件要带额外 dll（最典型的是 WinRT 投影：Microsoft.Windows.SDK.NET.dll + WinRT.Runtime.dll）。
# 宿主加载插件时会优先从插件目录解析依赖，所以这些 dll 必须一起装进包里，
# 否则用户那边一跑就 FileNotFoundException。
# 约定：输出目录里除主 dll 和契约 dll 之外的所有 dll 都算附属 dll。
# 注意：带附属 dll 的插件**必须**在 plugin.json 里写明 assembly 字段，
# 否则宿主按文件名排序挑主 dll 时会挑错（比如挑中 Microsoft.Windows.SDK.NET.dll）。
$extraDlls = @(Get-ChildItem -LiteralPath $outDir -Filter '*.dll' |
    Where-Object { $_.Name -ne $dllName -and $_.Name -ne 'MinecraftChatOverlay.Plugin.Abstractions.dll' })

# ---- 打 zip ----
$zipPath = Join-Path $packagesDir "$id-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    # 用 ArrayList 明确装"每一项都是一对(源文件, zip 内路径)"。
    # 注意别写成 $pairs = @(@($a,$b))：单个内层数组会被 PowerShell 展平成字符串数组，
    # 后面 $pair[0] 取到的是第一个字符，报"找不到文件 C:\...\C"这种莫名其妙的错。
    $pairs = New-Object System.Collections.ArrayList
    [void]$pairs.Add(@($dllPath, "$id/$dllName"))
    foreach ($extra in $extraDlls) {
        [void]$pairs.Add(@($extra.FullName, "$id/$($extra.Name)"))
    }
    [void]$pairs.Add(@($manifestPath, "$id/plugin.json"))
    foreach ($pair in $pairs) {
        $entry = $archive.CreateEntry($pair[1], [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        $bytes = [System.IO.File]::ReadAllBytes($pair[0])
        $entryStream.Write($bytes, 0, $bytes.Length)
        $entryStream.Close()
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

# ---- 找出同一个 id 的其它版本（先收集，等归档关掉再删：Windows 删不掉正被打开的文件）----
$stale = New-Object System.Collections.ArrayList
foreach ($other in @(Get-ChildItem -LiteralPath $packagesDir -Filter '*.zip')) {
    if ($other.FullName -eq $zipPath) { continue }

    $otherArchive = $null
    try {
        $otherArchive = [System.IO.Compression.ZipFile]::OpenRead($other.FullName)
        $entry = $otherArchive.Entries |
            Where-Object { $_.FullName -match '(?i)(^|[\\/])plugin\.json$' } |
            Sort-Object { ($_.FullName -split '[\\/]').Count } |
            Select-Object -First 1
        if ($entry) {
            $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
            $otherJson = $reader.ReadToEnd() | ConvertFrom-Json
            $reader.Close()
            if ("$($otherJson.id)" -eq $id) { [void]$stale.Add($other.FullName) }
        }
    }
    catch {
        Write-Warning "检查 $($other.Name) 失败：$_"
    }
    finally {
        if ($otherArchive) { $otherArchive.Dispose() }
    }
}

$removed = New-Object System.Collections.ArrayList
foreach ($stalePath in $stale) {
    try {
        Remove-Item -LiteralPath $stalePath -Force
        [void]$removed.Add((Split-Path $stalePath -Leaf))
    }
    catch {
        Write-Warning "删不掉 $stalePath ：$_"
    }
}

Write-Host "  插件   $id  v$version"
Write-Host "  主 dll $dllName"
Write-Host "  产出   $zipPath"
if ($removed.Count -gt 0) { Write-Host "  已删旧包 $($removed -join ', ')" }
Write-Output $zipPath