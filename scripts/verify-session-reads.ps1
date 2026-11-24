# 会话日志"读一致"校验: 把真实会话复制到 artifacts 下, 用**改动前**与**改动后**两套代码各读一遍,
# 比较指纹。任何事件、顺序、序号、负载、surface 操作的差异都会让指纹不同。
#
# 为什么要有它: 落盘格式级的改动(例如把回退改成只追加)一旦写错, 坏的是用户的会话日志。
# 所以所有这类改动都只在复制品上做, 并用这里的比较证明"同一份日志读出来的东西没变"。
#
# 用法:
#   pwsh -File scripts/verify-session-reads.ps1                       # 复制(缺则复制) + 双份转储 + 比较
#   pwsh -File scripts/verify-session-reads.ps1 -SkipBaselineBuild    # 基准已构建时跳过构建
#   pwsh -File scripts/verify-session-reads.ps1 -RefreshCopy           # 重新从真实目录复制
param(
    [string]$Source = "",
    [string]$CopyRoot = "",
    [string]$BaselineRef = "HEAD",
    [switch]$SkipBaselineBuild,
    [switch]$RefreshCopy
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $env:USERPROFILE ".dsh/sessions" }
if (-not $CopyRoot) { $CopyRoot = Join-Path $root "artifacts/real-sessions-copy/sessions" }
$dumps = Join-Path $root "artifacts/dumps"
$worktree = Join-Path $root "artifacts/head-worktree"
$baselineBuild = Join-Path $root "artifacts/head-build"
$currentBuild = Join-Path $root "artifacts/dump-build"
$tool = "tests/Dsh.SessionDump/Dsh.SessionDump.csproj"

# 1. 复制真实会话(只复制日志本身, 布局原样保留: 路径解析仍由生产代码决定)。
if ($RefreshCopy -or -not (Test-Path $CopyRoot)) {
    if (Test-Path $CopyRoot) { Remove-Item $CopyRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $CopyRoot | Out-Null
    $copied = 0
    foreach ($project in Get-ChildItem $Source -Directory) {
        foreach ($session in Get-ChildItem $project.FullName -Directory) {
            $log = Join-Path $session.FullName "session.jsonl.zstd"
            if (-not (Test-Path $log)) { continue }
            $target = Join-Path (Join-Path $CopyRoot $project.Name) $session.Name
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item $log $target -Force
            $copied++
        }
    }
    Write-Host "已复制会话: $copied"
}

New-Item -ItemType Directory -Force -Path $dumps | Out-Null

# 2. 基准: 在独立的 worktree 里用改动前的提交构建同一个转储工具。
if (-not $SkipBaselineBuild) {
    if (-not (Test-Path $worktree)) {
        git -C $root worktree add $worktree $BaselineRef | Out-Host
    }
    Copy-Item (Join-Path $root "tests/Dsh.SessionDump") (Join-Path $worktree "tests") -Recurse -Force
    dotnet build (Join-Path $worktree $tool) --artifacts-path $baselineBuild -v q -m:1
    if ($LASTEXITCODE -ne 0) { throw "基准转储工具构建失败" }
}

# 3. 当前树: 同一个工具, 同样的输入。
dotnet build (Join-Path $root $tool) --artifacts-path $currentBuild -v q -m:1
if ($LASTEXITCODE -ne 0) { throw "当前转储工具构建失败" }

$baseline = Join-Path $baselineBuild "bin/Dsh.SessionDump/debug/Dsh.SessionDump.exe"
$current = Join-Path $currentBuild "bin/Dsh.SessionDump/debug/Dsh.SessionDump.exe"
$baselineDump = Join-Path $dumps "baseline.txt"
$currentDump = Join-Path $dumps "current.txt"
& $baseline $CopyRoot $baselineDump | Out-Null
& $current $CopyRoot $currentDump | Out-Null

# 4. 比较。
$left = Get-Content $baselineDump
$right = Get-Content $currentDump
$difference = Compare-Object $left $right
if ($null -eq $difference) {
    Write-Host "读一致: $($left.Count) 行完全相同 ($baselineDump vs $currentDump)"
    exit 0
}
Write-Warning "读结果不一致: $($difference.Count) 行差异"
$difference | Select-Object -First 40 | Format-Table -Wrap | Out-Host
exit 1