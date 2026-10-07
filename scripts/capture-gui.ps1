param(
    [string]$DshHome = "",
    [string]$Session = "",
    [string]$Out = "artifacts020/gpu-screenshots/gui.png",
    [int]$WaitSeconds = 8,
    [switch]$KeepOpen,
    # 捕获目标可换: 默认 GUI 启动器; 传 -ExePath/-ExeArguments 可截 GPU 窗口(dsh tui --gpu)等其它进程。
    [string]$ExePath = "",
    [string]$ExeArguments = ""
)

# 后台截图 GUI: 不激活窗口, 不注入输入, 不占用用户的鼠标键盘。
# 需要看某个会话的界面时用 -Session <id> (等价于 dsh gui --session <id>), 启动后直接就是那个会话。
# 用 dsh-gui.exe(Windows 子系统)而不是控制台宿主, 这样连"命令行黑框一闪而过"都不会出现。
# 依赖: PrintWindow(PW_RENDERFULLCONTENT) 可以抓被遮挡的窗口。

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32GuiShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
"@
[Win32GuiShot]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

$root = Split-Path -Parent $PSScriptRoot
$exe = if ($ExePath.Length -gt 0) { $ExePath } else { Join-Path $root "DshGuiHost\bin\Debug\net10.0\dsh-gui.exe" }
if (-not (Test-Path $exe)) { throw "先构建目标启动器: $exe" }

$arguments = @()
if ($ExeArguments.Length -gt 0)
{
    $arguments += $ExeArguments.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
}
else
{
    if ($DshHome.Length -gt 0) { $arguments += @("--home", $DshHome) }
    if ($Session.Length -gt 0) { $arguments += @("--session", $Session) }
}
$errorLog = Join-Path $root "artifacts020\capture-gui.err"
Remove-Item $errorLog -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $root -RedirectStandardError $errorLog -PassThru

$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()
    if ($process.HasExited) { throw "gui 提前退出: $($process.ExitCode)" }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if ([Win32GuiShot]::IsIconic($process.MainWindowHandle)) { [Win32GuiShot]::ShowWindow($process.MainWindowHandle, 4) | Out-Null }
Start-Sleep -Seconds $WaitSeconds

$rect = New-Object Win32GuiShot+RECT
[Win32GuiShot]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
[Win32GuiShot]::PrintWindow($process.MainWindowHandle, $hdc, 2) | Out-Null
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()
$target = Join-Path $root $Out
$bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "saved $target ($width x $height)"
if (-not $KeepOpen) { Stop-Process -Id $process.Id -Force }
Get-Content $errorLog -ErrorAction SilentlyContinue | Select-Object -First 5
