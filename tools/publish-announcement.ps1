<#
.SYNOPSIS
  发一条开屏公告（改 about/announcement.json 并推送）。

.DESCRIPTION
  开屏公告 = 仓库里的 about/announcement.json。客户端启动时静默拉它
  （jsDelivr 主源 + GitHub raw 备用，取 updatedAt 更新的那份），时间戳比上次弹过的新才弹。

  所以"发公告"就是：换掉 id / updatedAt / 正文 -> push。本脚本会自动：
    1. updatedAt 填「现在」（本地时区 ISO 格式，客户端就认这个）
    2. id 默认按时间生成（id 只用于排查，判新旧靠 updatedAt）
    3. 同时写开发目录和仓库副本两份，否则下次 push.bat 的 robocopy 会用旧的那份盖回去
    4. 只 add/commit/push about/announcement.json，不动工作区里别的东西

.EXAMPLE
  .\publish-announcement.ps1 -Title "更新提示" -Body "更新了 1.2.8`n· 修了 xxx" -Link "https://github.com/goldiamond1031/MinecraftChatOverlay/releases" -LinkText "有可用更新"
.EXAMPLE
  .\publish-announcement.ps1 -Show
  .\publish-announcement.ps1 -Disable
  .\publish-announcement.ps1 -Title x -Body y -DryRun
  .\publish-announcement.ps1 -BodyFile body.txt
#>
[CmdletBinding()]
param(
    [string]$Title = "",
    [string]$Body = "",
    [string]$BodyFile = "",
    [string]$Link = "",
    [string]$LinkText = "",
    [string]$Id = "",
    [switch]$Disable,
    [switch]$NoPush,
    [switch]$DryRun,
    [switch]$Show
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 找 git 仓库：脚本上一级有 .git 就用它（从仓库里那份 tools\ 跑），
# 否则用固定路径 —— 开发目录 C:\MinecraftChatOverlayDSUI3 里没有 .git，
# 直接拿它当仓库会报 "fatal: not a git repository"。
function Resolve-Repo {
    $up = Split-Path -Parent $PSScriptRoot
    if (Test-Path (Join-Path $up '.git')) { return $up }
    $fixed = 'C:\Github\MinecraftChatOverlay'
    if (Test-Path (Join-Path $fixed '.git')) { return $fixed }
    throw ("找不到 git 仓库：脚本上一级不是仓库（$up），也没有 $fixed")
}
$Repo = Resolve-Repo
$Dev = 'C:\MinecraftChatOverlayDSUI3'
$Rel = 'about\announcement.json'
$RepoPath = Join-Path $Repo $Rel
$DevPath = Join-Path $Dev $Rel

function Get-Ann([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try { return (Get-Content $path -Encoding UTF8 -Raw | ConvertFrom-Json) } catch { return $null }
}

function Esc([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return "" }
    $s = $s -replace "`r`n", "`n"
    $s = $s -replace "`r", "`n"
    $s = $s -replace '\\', '\\'
    $s = $s -replace '"', '\"'
    $s = $s -replace "`n", '\n'
    $s = $s -replace "`t", '\t'
    return $s
}

if ($Show) {
    $a = Get-Ann $RepoPath
    if ($null -eq $a) {
        Write-Host "仓库里还没有 about\announcement.json。" -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host ("id         = " + $a.id)
        Write-Host ("updatedAt  = " + $a.updatedAt)
        Write-Host ("enabled    = " + $a.enabled)
        Write-Host ("title      = " + $a.title)
        Write-Host ("link       = " + $a.link)
        Write-Host "body       ="
        Write-Host ("  " + (($a.body -split "`n") -join "`n  "))
        Write-Host ""
    }
    return
}

if (-not [string]::IsNullOrWhiteSpace($BodyFile)) {
    if (-not (Test-Path $BodyFile)) { throw "找不到正文文件：$BodyFile" }
    $Body = Get-Content $BodyFile -Encoding UTF8 -Raw
    $Body = $Body -replace "`r`n", "`n"
    $Body = $Body.TrimEnd("`n")
}

# 闸门：标题和正文都空着就拒绝 —— 否则会写出一条空公告把线上那条盖掉（踩过一次）
if (-not $Disable -and $DryRun -eq $false -and [string]::IsNullOrWhiteSpace($Title) -and [string]::IsNullOrWhiteSpace($Body)) {
    throw "标题和正文都是空的，不发。要关掉公告请用 -Disable。"
}

if ([string]::IsNullOrWhiteSpace($Id)) { $Id = (Get-Date -Format 'yyyy-M-d-HHmm') }
$now = (Get-Date -Format 'yyyy-MM-ddTHH:mm:sszzz')
$enabled = if ($Disable) { 'false' } else { 'true' }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    "schemaVersion": 1,')
[void]$sb.AppendLine(('    "id": "' + (Esc $Id) + '",'))
[void]$sb.AppendLine(('    "updatedAt": "' + $now + '",'))
[void]$sb.AppendLine(('    "enabled": ' + $enabled + ','))
[void]$sb.AppendLine(('    "title": "' + (Esc $Title) + '",'))
if ([string]::IsNullOrWhiteSpace($Link)) {
    [void]$sb.AppendLine(('    "body": "' + (Esc $Body) + '"'))
} else {
    [void]$sb.AppendLine(('    "body": "' + (Esc $Body) + '",'))
    [void]$sb.AppendLine(('    "link": "' + (Esc $Link) + '",'))
    [void]$sb.AppendLine(('    "linkText": "' + (Esc $LinkText) + '"'))
}
[void]$sb.Append('}')
$json = $sb.ToString()

if ($DryRun) {
    Write-Host "===== 预览（-DryRun：没写文件、没推送）====="
    Write-Host $json
    return
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($RepoPath, $json, $utf8NoBom)
if (Test-Path (Split-Path -Parent $DevPath)) {
    [System.IO.File]::WriteAllText($DevPath, $json, $utf8NoBom)
    Write-Host "已写入："
    Write-Host ("  " + $RepoPath)
    Write-Host ("  " + $DevPath)
} else {
    Write-Host ("已写入：" + $RepoPath + "（开发目录不存在，跳过）")
}

Write-Host ""
Write-Host "这条公告：" -ForegroundColor Cyan
Write-Host ("  id        = " + $Id)
Write-Host ("  updatedAt = " + $now + "     <- 客户端比的就是它")
Write-Host ("  enabled   = " + $enabled)
Write-Host ("  title     = " + $Title)

if ($NoPush) {
    Write-Host ""
    Write-Host "-NoPush：已写好，没提交没推送。" -ForegroundColor Yellow
    return
}

Push-Location $Repo
try {
    git add -- about/announcement.json
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host "内容和仓库里一模一样，不用提交。" -ForegroundColor Yellow
    } else {
        git commit -m ("公告：" + $Title) | Out-Host
        git push origin main | Out-Host
    }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "推送完。核对地址（jsDelivr 有缓存，raw 是即时的）：" -ForegroundColor Green
Write-Host "  https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/about/announcement.json"
Write-Host "  https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/about/announcement.json"
Write-Host "客户端下次启动就会弹（比 updatedAt，比上次弹过的新才弹）。"
