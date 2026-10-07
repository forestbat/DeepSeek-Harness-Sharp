# DeepSeek Harness 命令行安装脚本(Windows)。
# 从 GitHub Release 下载对应架构的构建, 装到用户目录, 并注册 `dsharp` 命令。
#
# 用法:
#   irm https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.ps1 | iex
#
# 可用环境变量覆盖:
#   DSHARP_VERSION        版本号或 latest(默认 latest)
#   DSHARP_HOME           安装目录(默认 %LOCALAPPDATA%\Programs\dsharp)
#   DSHARP_BASE_URL       Release 基址(默认 GitHub Release, 可指向镜像)
#   DSHARP_LOCAL_ARCHIVE  本地归档路径, 跳过下载(离线安装/测试用)
#   DSHARP_PROXY          下载代理, 例如 http://127.0.0.1:10808
#
# Release 资产命名约定: dsharp-win-<arch>.zip, arch 为 x64 或 arm64。
# 归档根目录即发布输出(含 DeepSeek-Harness-Sharp.exe 与 plugins/)。
# 注意: 本脚本要能安全地通过 irm | iex 运行, 因此不调用 exit。
$ErrorActionPreference = 'Stop'

$DsharpRepo = 'forestbat/DeepSeek-Harness-Sharp'
$DsharpVersion = if ($env:DSHARP_VERSION) { $env:DSHARP_VERSION } else { 'latest' }
$DsharpInstallDir = if ($env:DSHARP_HOME) { $env:DSHARP_HOME } else { Join-Path $env:USERPROFILE '.dsharp' }
$DsharpBaseUrl = if ($env:DSHARP_BASE_URL) { $env:DSHARP_BASE_URL } else { "https://github.com/$DsharpRepo/releases" }
$DsharpLocalArchive = $env:DSHARP_LOCAL_ARCHIVE
$DsharpProxy = $env:DSHARP_PROXY
$DsharpCommand = 'dsharp'
$DsharpHostExe = 'DeepSeek-Harness-Sharp.exe'

function Get-DsharpArch {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "不支持的 CPU 架构: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    }
}

$DsharpRid = "win-$(Get-DsharpArch)"
$DsharpAsset = "dsharp-$DsharpRid.zip"
$DsharpUrl = if ($DsharpVersion -eq 'latest') {
    "$DsharpBaseUrl/latest/download/$DsharpAsset"
}
else {
    $tag = if ($DsharpVersion.StartsWith('v')) { $DsharpVersion } else { "v$DsharpVersion" }
    "$DsharpBaseUrl/download/$tag/$DsharpAsset"
}

$DsharpTmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsharp-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $DsharpTmp | Out-Null
try {
    $zip = Join-Path $DsharpTmp $DsharpAsset
    if ($DsharpLocalArchive) {
        if (-not (Test-Path -LiteralPath $DsharpLocalArchive)) { throw "本地归档不存在: $DsharpLocalArchive" }
        Copy-Item -LiteralPath $DsharpLocalArchive -Destination $zip
        Write-Host "使用本地归档: $DsharpLocalArchive"
    }
    else {
        Write-Host "下载 $DsharpUrl"
        $request = @{ Uri = $DsharpUrl; OutFile = $zip; UseBasicParsing = $true }
        if ($DsharpProxy) { $request.Proxy = $DsharpProxy }
        Invoke-WebRequest @request
    }

    $extract = Join-Path $DsharpTmp 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force

    $hostFile = Get-ChildItem -LiteralPath $extract -Recurse -Filter $DsharpHostExe | Select-Object -First 1
    if (-not $hostFile) { throw "归档里找不到 $DsharpHostExe" }
    $srcDir = $hostFile.DirectoryName

    if (Test-Path -LiteralPath $DsharpInstallDir) { Remove-Item -LiteralPath $DsharpInstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $DsharpInstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $srcDir '*') -Destination $DsharpInstallDir -Recurse -Force

    $shim = Join-Path $DsharpInstallDir "$DsharpCommand.cmd"
    Set-Content -LiteralPath $shim -Encoding ASCII -Value @(
        '@echo off',
        "`"%~dp0$DsharpHostExe`" %*"
    )

    # 把安装目录写入用户 PATH(持久), 并刷新当前会话。
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ -ne '' })
    if ($entries -notcontains $DsharpInstallDir) {
        $newPath = (@($entries) + $DsharpInstallDir) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Host "已把 $DsharpInstallDir 加入用户 PATH(新开终端生效)"
    }
    $env:Path = "$env:Path;$DsharpInstallDir"

    $legacy = Get-Command dshsh -ErrorAction SilentlyContinue
    if ($legacy) {
        Write-Host "提示: 检测到旧命令 'dshsh'($($legacy.Source)), 新版命令是 'dsharp'; 可删除旧的。"
    }

    Write-Host ""
    Write-Host "已安装 $DsharpCommand -> $(Join-Path $DsharpInstallDir $DsharpHostExe)"
    Write-Host "版本: $DsharpVersion (平台 $DsharpRid)"
    Write-Host "直接运行: $DsharpCommand"
}
finally {
    Remove-Item -LiteralPath $DsharpTmp -Recurse -Force -ErrorAction SilentlyContinue
}
