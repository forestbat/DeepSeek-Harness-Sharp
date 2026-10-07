# 把 GUI 挂到当前用户的桌面: 新建「DeepSeek Harness」快捷方式(.lnk), 指向 dsh-gui.exe。
# 用法: pwsh -File scripts/install-desktop.ps1 [-Target <dsh-gui.exe 路径>] [-Icon <icon.ico 路径>]
[CmdletBinding()]
param (
    [string]$Target = "",
    [string]$Icon = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

if (-not $Target) {
    $candidates = @(
        (Join-Path $repoRoot "DshGuiHost\bin\Release\net10.0\dsh-gui.exe"),
        (Join-Path $repoRoot "DshGuiHost\bin\Debug\net10.0\dsh-gui.exe"),
        (Join-Path $repoRoot "artifacts020\gui\dsh-gui.exe")
    )
    $Target = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $Target -or -not (Test-Path -LiteralPath $Target)) {
    Write-Error "找不到 dsh-gui.exe; 先构建 DshGuiHost, 或用 -Target 指定路径"
}

if (-not $Icon -or -not (Test-Path -LiteralPath $Icon)) {
    $Icon = Join-Path $repoRoot "src\Dsh.Gui\Assets\icon.ico"
}

$targetPath = (Resolve-Path -LiteralPath $Target).Path
$iconPath = if (Test-Path -LiteralPath $Icon) { (Resolve-Path -LiteralPath $Icon).Path } else { "" }
$desktop = [Environment]::GetFolderPath("Desktop")
$linkPath = Join-Path $desktop "DeepSeek Harness.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($linkPath)
$shortcut.TargetPath = $targetPath
$shortcut.WorkingDirectory = Split-Path -Parent $targetPath
$shortcut.Description = "DeepSeek Harness"
if ($iconPath) { $shortcut.IconLocation = $iconPath }
$shortcut.Save()

Write-Output "已创建: $linkPath"
Write-Output "目标: $targetPath"
Write-Output "图标: $($iconPath ? $iconPath : '未设置')"
