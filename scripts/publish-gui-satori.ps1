# 用 Satori GC 发布 GUI(dsh-gui)的可选流程。
#
# 为什么是脚本而不是直接改 csproj: Satori GC 只在**发布时**换运行时, 靠一个 MSBuild SDK
# (PublishWithSatoriGC)注入; 把那个 SDK 写进 Dsh.GuiHost.csproj 会让每次常规构建都去还原一个实验性包。
# 所以这里把 Dsh.GuiHost 复制到 artifacts 下、注入 SDK 后发布, 发完即删 —— 常规构建与源码树都不受影响。
#
# 实测(百万条事件会话, 见 plans/会话完整加载与传输优化-方案-2026-11-20.md §2.1):
#   Satori 把 GC 停顿从 2.7s 压到 57ms, 解析时间比 server GC 再省 14%; 分配量不变, 峰值常驻高 15%~37%。
#
# 用法:
#   pwsh -File scripts/publish-gui-satori.ps1                     # 默认 win-x64, 输出 artifacts/satori-gui
#   pwsh -File scripts/publish-gui-satori.ps1 -UseSatoriGC:$false # 同一套流程发 stock 运行时(做对照)
param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Output = "",
    [string]$SatoriSdkVersion = "11.0.1-satori.44643583273.1",
    [bool]$UseSatoriGC = $true
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "Dsh.GuiHost"
$work = Join-Path $root "artifacts/satori-gui-src"
if (-not $Output) { $Output = Join-Path $root "artifacts/satori-gui" }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item (Join-Path $source "Dsh.GuiHost.csproj") $work
Copy-Item (Join-Path $source "Program.cs") $work

$project = Join-Path $work "Dsh.GuiHost.satori.csproj"
$original = Get-Content (Join-Path $work "Dsh.GuiHost.csproj") -Raw
$patched = $original.Replace(
    '<Project Sdk="Microsoft.NET.Sdk">',
    "<Project Sdk=`"Microsoft.NET.Sdk;PublishWithSatoriGC/$SatoriSdkVersion`">")
if ($patched -eq $original) { throw "Dsh.GuiHost.csproj 的 Sdk 属性不是预期形态, 无法注入 Satori SDK" }
# 复制出来的项目比原项目深一层, 相对路径因此都多一级。
$patched = $patched.Replace('..\', '..\..\')
$patched = $patched.Replace(
    "</PropertyGroup>",
    "  <UseSatoriGC>$($UseSatoriGC.ToString().ToLowerInvariant())</UseSatoriGC>`r`n  </PropertyGroup>", 1)
Set-Content -Path $project -Value $patched -NoNewline
Remove-Item (Join-Path $work "Dsh.GuiHost.csproj")

try {
    dotnet publish $project `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishReadyToRun=false `
        -o $Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败(exit $LASTEXITCODE)" }
    Write-Host "已发布: $Output (Satori=$UseSatoriGC)"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}