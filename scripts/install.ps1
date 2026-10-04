# DeepSeek Harness 命令行安装脚本(Windows)。
# 从 GitHub Release 下载对应架构的构建, 装到用户目录, 并注册 `dshsh` 命令。
#
# 用法:
#   irm https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.ps1 | iex
#
# 可用环境变量覆盖:
#   DSHSH_VERSION        版本号或 latest(默认 latest)
#   DSHSH_HOME           安装目录(默认 %LOCALAPPDATA%\Programs\dshsh)
#   DSHSH_BASE_URL       Release 基址(默认 GitHub Release, 可指向镜像)
#   DSHSH_LOCAL_ARCHIVE  本地归档路径, 跳过下载(离线安装/测试用)
#   DSHSH_PROXY          下载代理, 例如 http://127.0.0.1:10808
#
# Release 资产命名约定: dshsh-win-<arch>.zip, arch 为 x64 或 arm64。
# 归档根目录即发布输出(含 DeepSeek-Harness-Sharp.exe 与 plugins/)。
# 注意: 本脚本要能安全地通过 irm | iex 运行, 因此不调用 exit。
$ErrorActionPreference = 'Stop'

$DshshRepo = 'forestbat/DeepSeek-Harness-Sharp'
$DshshVersion = if ($env:DSHSH_VERSION) { $env:DSHSH_VERSION } else { 'latest' }
$DshshInstallDir = if ($env:DSHSH_HOME) { $env:DSHSH_HOME } else { Join-Path $env:LOCALAPPDATA 'Programs\dshsh' }
$DshshBaseUrl = if ($env:DSHSH_BASE_URL) { $env:DSHSH_BASE_URL } else { "https://github.com/$DshshRepo/releases" }
$DshshLocalArchive = $env:DSHSH_LOCAL_ARCHIVE
$DshshProxy = $env:DSHSH_PROXY
$DshshCommand = 'dshsh'
$DshshHostExe = 'DeepSeek-Harness-Sharp.exe'

function Get-DshshArch {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "不支持的 CPU 架构: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    }
}

$DshshRid = "win-$(Get-DshshArch)"
$DshshAsset = "dshsh-$DshshRid.zip"
$DshshUrl = if ($DshshVersion -eq 'latest') {
    "$DshshBaseUrl/latest/download/$DshshAsset"
}
else {
    $tag = if ($DshshVersion.StartsWith('v')) { $DshshVersion } else { "v$DshshVersion" }
    "$DshshBaseUrl/download/$tag/$DshshAsset"
}

$DshshTmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dshsh-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $DshshTmp | Out-Null
try {
    $zip = Join-Path $DshshTmp $DshshAsset
    if ($DshshLocalArchive) {
        if (-not (Test-Path -LiteralPath $DshshLocalArchive)) { throw "本地归档不存在: $DshshLocalArchive" }
        Copy-Item -LiteralPath $DshshLocalArchive -Destination $zip
        Write-Host "使用本地归档: $DshshLocalArchive"
    }
    else {
        Write-Host "下载 $DshshUrl"
        $request = @{ Uri = $DshshUrl; OutFile = $zip; UseBasicParsing = $true }
        if ($DshshProxy) { $request.Proxy = $DshshProxy }
        Invoke-WebRequest @request
    }

    $extract = Join-Path $DshshTmp 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force

    $hostFile = Get-ChildItem -LiteralPath $extract -Recurse -Filter $DshshHostExe | Select-Object -First 1
    if (-not $hostFile) { throw "归档里找不到 $DshshHostExe" }
    $srcDir = $hostFile.DirectoryName

    if (Test-Path -LiteralPath $DshshInstallDir) { Remove-Item -LiteralPath $DshshInstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $DshshInstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $srcDir '*') -Destination $DshshInstallDir -Recurse -Force

    $shim = Join-Path $DshshInstallDir "$DshshCommand.cmd"
    Set-Content -LiteralPath $shim -Encoding ASCII -Value @(
        '@echo off',
        "`"%~dp0$DshshHostExe`" %*"
    )

    # 把安装目录写入用户 PATH(持久), 并刷新当前会话。
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ -ne '' })
    if ($entries -notcontains $DshshInstallDir) {
        $newPath = (@($entries) + $DshshInstallDir) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Host "已把 $DshshInstallDir 加入用户 PATH(新开终端生效)"
    }
    $env:Path = "$env:Path;$DshshInstallDir"

    Write-Host ""
    Write-Host "已安装 $DshshCommand -> $(Join-Path $DshshInstallDir $DshshHostExe)"
    Write-Host "版本: $DshshVersion (平台 $DshshRid)"
    Write-Host "直接运行: $DshshCommand"
}
finally {
    Remove-Item -LiteralPath $DshshTmp -Recurse -Force -ErrorAction SilentlyContinue
}
