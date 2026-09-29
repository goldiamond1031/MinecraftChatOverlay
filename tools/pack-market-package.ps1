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
$outDir = Join-Path $PluginDir 'bin\Release\net8.0-windows'
if (-not (Test-Path -LiteralPath $outDir)) { throw "没有编译产物：$outDir（先编译插件）" }

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

# ---- 打 zip ----
$zipPath = Join-Path $packagesDir "$id-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $pairs = @(@($dllPath, "$id/$dllName"), @($manifestPath, "$id/plugin.json"))
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