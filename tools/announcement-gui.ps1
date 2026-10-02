<#
  发开屏公告 —— 图形界面版（双击 announcement.bat 打开）
  填标题/正文/链接 -> 点【提交并推送】，它会调用同目录的 publish-announcement.ps1 去写文件 + push。
  打开时会自动把当前这条公告读进来，方便在原基础上改。
#>
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ErrorActionPreference = 'Stop'
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
$Cli = Join-Path $PSScriptRoot 'publish-announcement.ps1'
$AnnPath = Join-Path $Repo 'about\announcement.json'

$font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.5)
$form = New-Object System.Windows.Forms.Form
$form.Text = '发开屏公告 - MinecraftChatOverlay'
$form.ClientSize = New-Object System.Drawing.Size(640, 560)
$form.StartPosition = 'CenterScreen'
$form.Font = $font
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false

function Add-Label($text, $x, $y, $w) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y); $l.Size = New-Object System.Drawing.Size($w, 22)
    $form.Controls.Add($l); return $l
}

[void](Add-Label '标题' 14 15 70)
$titleBox = New-Object System.Windows.Forms.TextBox
$titleBox.Location = New-Object System.Drawing.Point(92, 12); $titleBox.Size = New-Object System.Drawing.Size(532, 26)
$form.Controls.Add($titleBox)

[void](Add-Label '正文' 14 52 70)
$bodyBox = New-Object System.Windows.Forms.TextBox
$bodyBox.Location = New-Object System.Drawing.Point(92, 49); $bodyBox.Size = New-Object System.Drawing.Size(532, 240)
$bodyBox.Multiline = $true; $bodyBox.AcceptsReturn = $true; $bodyBox.ScrollBars = 'Vertical'
$bodyBox.Font = $font
$form.Controls.Add($bodyBox)

[void](Add-Label '链接' 14 302 70)
$linkBox = New-Object System.Windows.Forms.TextBox
$linkBox.Location = New-Object System.Drawing.Point(92, 299); $linkBox.Size = New-Object System.Drawing.Size(532, 26)
$form.Controls.Add($linkBox)

[void](Add-Label '按钮文字' 14 336 70)
$linkTextBox = New-Object System.Windows.Forms.TextBox
$linkTextBox.Location = New-Object System.Drawing.Point(92, 333); $linkTextBox.Size = New-Object System.Drawing.Size(532, 26)
$form.Controls.Add($linkTextBox)

$enabledBox = New-Object System.Windows.Forms.CheckBox
$enabledBox.Text = '启用（不勾 = 发一条"关闭公告"，内容留着下次用）'
$enabledBox.Location = New-Object System.Drawing.Point(92, 368); $enabledBox.Size = New-Object System.Drawing.Size(420, 24)
$enabledBox.Checked = $true
$form.Controls.Add($enabledBox)

$submit = New-Object System.Windows.Forms.Button
$submit.Text = '提交并推送'; $submit.Location = New-Object System.Drawing.Point(92, 400); $submit.Size = New-Object System.Drawing.Size(130, 34)
$form.Controls.Add($submit)

$preview = New-Object System.Windows.Forms.Button
$preview.Text = '只看生成的 JSON'; $preview.Location = New-Object System.Drawing.Point(232, 400); $preview.Size = New-Object System.Drawing.Size(140, 34)
$form.Controls.Add($preview)

$reload = New-Object System.Windows.Forms.Button
$reload.Text = '重新读当前公告'; $reload.Location = New-Object System.Drawing.Point(382, 400); $reload.Size = New-Object System.Drawing.Size(140, 34)
$form.Controls.Add($reload)

$status = New-Object System.Windows.Forms.TextBox
$status.Location = New-Object System.Drawing.Point(92, 444); $status.Size = New-Object System.Drawing.Size(532, 100)
$status.Multiline = $true; $status.ReadOnly = $true; $status.ScrollBars = 'Vertical'
$status.BackColor = [System.Drawing.Color]::White
$form.Controls.Add($status)

function Load-Current {
    if (-not (Test-Path $AnnPath)) { return }
    try {
        $a = Get-Content $AnnPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $titleBox.Text = [string]$a.title
        $bodyBox.Text = ([string]$a.body) -replace '\\n', "`r`n"
        $linkBox.Text = [string]$a.link
        $linkTextBox.Text = [string]$a.linkText
        $enabledBox.Checked = [bool]$a.enabled
        $status.Text = "已读入当前公告：id=" + $a.id + "  updatedAt=" + $a.updatedAt
    } catch { $status.Text = "当前公告读不出来（不影响发布）：" + $_.Exception.Message }
}

function Invoke-Cli([bool]$dryRun) {
    $tmp = Join-Path $env:TEMP ('ann_body_' + [guid]::NewGuid().ToString('N') + '.txt')
    [System.IO.File]::WriteAllText($tmp, $bodyBox.Text, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Cli, '-BodyFile', $tmp, '-Title', $titleBox.Text)
        if ($linkBox.Text.Trim().Length -gt 0) {
            $a += @('-Link', $linkBox.Text.Trim(), '-LinkText', $linkTextBox.Text.Trim())
        }
        if (-not $enabledBox.Checked) { $a += '-Disable' }
        if ($dryRun) { $a += '-DryRun' }
        $out = & powershell @a 2>&1
        return ($out | Out-String)
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

$preview.Add_Click({
    try { $status.Text = Invoke-Cli $true } catch { [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '出错') }
})

$reload.Add_Click({ Load-Current })

$submit.Add_Click({
    if ($titleBox.Text.Trim().Length -eq 0 -and $bodyBox.Text.Trim().Length -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show('标题和正文至少填一个。', '还差点东西')
        return
    }
    $ok = [System.Windows.Forms.MessageBox]::Show(
        "确认发布这条公告并推送？`r`n`r`n标题：" + $titleBox.Text + "`r`n`r`n（推送后客户端下次启动会弹）",
        '确认发布', 'OKCancel', 'Question')
    if ($ok -ne 'OK') { return }

    $submit.Enabled = $false; $form.Cursor = 'WaitCursor'
    try {
        $out = Invoke-Cli $false
        $status.Text = $out
        [void][System.Windows.Forms.MessageBox]::Show("完事。`r`n`r`n" + $out, '发公告', 'OK', 'Information')
    } catch {
        $status.Text = $_.Exception.Message
        [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '发布失败', 'OK', 'Error')
    } finally {
        $form.Cursor = 'Default'; $submit.Enabled = $true
    }
})

Load-Current
[void]$form.ShowDialog()
