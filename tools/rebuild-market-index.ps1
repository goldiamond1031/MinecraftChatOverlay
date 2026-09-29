<#
.SYNOPSIS
    重建插件市场清单 market\index.json。

.DESCRIPTION
    市场没有服务器：清单就是一个 JSON，插件包就是 market\packages\ 里的 zip。
    这个脚本负责把两者对上 —— 读每个 zip 里的 plugin.json，算 SHA256，生成清单。

    加一个新插件 / 更新一个插件的流程：
      1) 把 zip 放进仓库的 market\packages\（网页上传或本地放都行）
      2) 跑这个脚本（从 GitHub 扫： -FromRepo；只扫本地目录：不加参数）
      3) push.bat 把 index.json 推上去

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\rebuild-market-index.ps1 -FromRepo -SyncPackages
#>
[CmdletBinding()]
param(
    [string]$Owner = 'goldiamond1031',
    [string]$Repo = 'MinecraftChatOverlay',
    [string]$Branch = 'main',

    # 从 GitHub 仓库扫（而不是只扫本地目录）—— 适合"我用网页上传的 zip"
    [switch]$FromRepo,

    # 从仓库扫的时候，顺便把 zip 存一份到本地 market\packages\（这样本地就是全的）
    [switch]$SyncPackages
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$packagesDir = Join-Path $root 'market\packages'
$outFile = Join-Path $root 'market\index.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $packagesDir)) { New-Item -ItemType Directory -Path $packagesDir | Out-Null }

$temp = Join-Path $env:TEMP ('mco-market-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $zips = New-Object System.Collections.ArrayList

    if ($FromRepo) {
        $api = "https://api.github.com/repos/$Owner/$Repo/contents/market/packages?ref=$Branch"
        Write-Host "从仓库扫：$api"
        $items = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'MCO-Market-Index' } -TimeoutSec 30
        foreach ($item in $items) {
            if ($item.name -notmatch '(?i)\.zip$') { continue }
            $dest = Join-Path $temp $item.name
            Invoke-WebRequest -Uri $item.download_url -OutFile $dest -TimeoutSec 60 -UseBasicParsing
            [void]$zips.Add([pscustomobject]@{ Name = $item.name; Path = $dest })
        }
    }
    else {
        Write-Host "扫本地目录：$packagesDir"
        foreach ($file in (Get-ChildItem -LiteralPath $packagesDir -Filter '*.zip' -ErrorAction SilentlyContinue)) {
            [void]$zips.Add([pscustomobject]@{ Name = $file.Name; Path = $file.FullName })
        }
    }

    if ($zips.Count -eq 0) {
        Write-Warning "一个 zip 都没找到，清单会是空的。"
    }

    $plugins = New-Object System.Collections.ArrayList

    foreach ($zip in $zips) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.Path)
        try {
            # plugin.json 可能在根目录，也可能套一层目录 —— 取层级最浅的那个
            $entry = $archive.Entries |
                Where-Object { $_.FullName -match '(?i)(^|/)plugin\.json$' } |
                Sort-Object { ($_.FullName -split '/').Count } |
                Select-Object -First 1

            if (-not $entry) {
                Write-Warning "$($zip.Name)：里面没有 plugin.json，跳过"
                continue
            }

            $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            $reader.Close()

            if ([string]::IsNullOrWhiteSpace($manifest.id)) {
                Write-Warning "$($zip.Name)：plugin.json 里没有 id，跳过"
                continue
            }

            $sha = (Get-FileHash -LiteralPath $zip.Path -Algorithm SHA256).Hash
            $size = (Get-Item -LiteralPath $zip.Path).Length

            $record = [ordered]@{
                id           = "$($manifest.id)"
                name         = "$($manifest.name)"
                version      = "$($manifest.version)"
                author       = "$($manifest.author)"
                description  = "$($manifest.description)"
                apiVersion   = [int]$manifest.apiVersion
                capabilities = @($manifest.capabilities)
                downloadUrl  = "packages/$($zip.Name)"
                sha256       = $sha
                size         = $size
                tags         = @()
            }
            [void]$plugins.Add($record)
            Write-Host ("  + {0}  v{1}  ({2:N0} 字节)  ← {3}" -f $record.id, $record.version, $size, $zip.Name)

            if ($FromRepo -and $SyncPackages) {
                $localCopy = Join-Path $packagesDir $zip.Name
                Copy-Item -LiteralPath $zip.Path -Destination $localCopy -Force
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $sorted = @($plugins | Sort-Object { $_['id'] })
    $index = [ordered]@{
        schemaVersion = 1
        updatedAt     = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        plugins       = $sorted
    }

    $json = $index | ConvertTo-Json -Depth 8
    if ($sorted.Count -le 1 -and $json -notmatch '"plugins"\s*:\s*\[') {
        # 单元素数组被 ConvertTo-Json 展平的兜底：手工补成数组
        $json = $json -replace '"plugins"\s*:\s*\{', '"plugins": [{'
        $json = $json.TrimEnd()
        if ($json.EndsWith('}')) { $json = $json.Substring(0, $json.Length - 1) + '}]}' }
    }

    [System.IO.File]::WriteAllText($outFile, $json, $utf8)
    Write-Host ""
    Write-Host "已写出 $outFile（$($sorted.Count) 个插件）"
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}