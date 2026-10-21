#requires -Version 7
# Explicitly provision the optional native test dependency from one verified source revision.
# Windows builds require PowerShell 7: pwsh -NoProfile -File scripts/fetch-iroh-ssh.ps1 -Rid win-x64
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'win-x64', 'win-arm64')]
    [string]$Rid
)
$ErrorActionPreference = 'Stop'
$RootDir = Split-Path -Parent $PSScriptRoot
$Revision = '82e99719f8b4e44c69622140f4acb25e5ae54184'
$SourceSha256 = 'cfcafde6b789b076bd6e4c28abbc9a92476db122efa5d644bdded35380d6a8ac'
$PatchSha256 = 'e383bd31cfe47d8ac57bb732ee53f31f24f6d94e8ec48d5a41cd59d26ed4f58c'
$Patch = Join-Path $PSScriptRoot 'iroh-ssh-readiness.patch'
if ((Get-FileHash -LiteralPath $Patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $PatchSha256) { throw 'iroh-ssh readiness patch SHA256 mismatch.' }
$Toolchain = '1.93.0'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not $Rid.StartsWith('win-')) {
    throw "Cannot build $Rid with the Windows helper; use fetch-iroh-ssh.sh on a matching Linux/macOS build host with the target compiler and sysroot."
}
$Target = if ($Rid -eq 'win-x64') { 'x86_64-pc-windows-msvc' } else { 'aarch64-pc-windows-msvc' }
$File = 'iroh-ssh.exe'
$WorkDir = [System.IO.Path]::GetFullPath((Join-Path $RootDir "artifacts/iroh-ssh/$Rid"))
$Output = Join-Path $WorkDir $File
$Stamp = Join-Path $WorkDir 'build.sha256'
$License = Join-Path $WorkDir 'LICENSE.txt'
$ThirdParty = Join-Path $WorkDir 'THIRD-PARTY-LICENSES.txt'
$FixturePatch = Join-Path $WorkDir 'READINESS.patch'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$Sha = [System.Security.Cryptography.SHA256]::Create()
try { $LockHash = [BitConverter]::ToString($Sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($WorkDir.ToUpperInvariant()))).Replace('-', '') }
finally { $Sha.Dispose() }
$Mutex = [System.Threading.Mutex]::new($false, "Global\DshIrohSsh-$LockHash")
$Acquired = $false
$StageDir = $null
try {
    try { [void]$Mutex.WaitOne(); $Acquired = $true }
    catch [System.Threading.AbandonedMutexException] { $Acquired = $true }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'iroh-ssh.NOTICE.txt') -Destination (Join-Path $WorkDir 'NOTICE.txt') -Force
    if ((Test-Path -LiteralPath $Output) -and (Test-Path -LiteralPath $Stamp) -and (Test-Path -LiteralPath $License) -and (Test-Path -LiteralPath $ThirdParty) -and (Test-Path -LiteralPath $FixturePatch) -and (Get-FileHash -LiteralPath $FixturePatch -Algorithm SHA256).Hash.ToLowerInvariant() -eq $PatchSha256) {
        $Hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
        $LicenseHash = (Get-FileHash -LiteralPath $License -Algorithm SHA256).Hash.ToLowerInvariant()
        $ThirdPartyHash = (Get-FileHash -LiteralPath $ThirdParty -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-Content -LiteralPath $Stamp -Raw).Trim() -eq "v4 $Revision $PatchSha256 $Toolchain $Target $Hash $LicenseHash $ThirdPartyHash") { return }
    }
    # Windows tar handles drive-letter archives even when Git Bash supplied the parent PATH.
    $Tar = Join-Path ([Environment]::GetFolderPath('System')) 'tar.exe'
    if (-not (Test-Path -LiteralPath $Tar)) { throw 'Building the optional iroh-ssh test dependency requires the Windows system tar.exe.' }
    foreach ($Command in @('rustup', 'cargo', 'git')) {
        if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) { throw "Building the optional iroh-ssh test dependency requires $Command." }
    }
    $StageDir = Join-Path $WorkDir ".stage-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $StageDir | Out-Null
    $HostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($HostArch -notin @('x64', 'arm64')) { throw "Unsupported Windows build-host architecture: $HostArch." }
    $TargetArch = if ($Rid -eq 'win-x64') { 'x64' } else { 'arm64' }
    $VsWhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $VsWhere)) { throw 'Install Visual Studio Build Tools with MSVC C++ tools and a Windows SDK.' }
    $VsRoot = & $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VsRoot) { throw 'No Visual Studio MSVC C++ installation was found.' }
    $MsvcRoot = Join-Path $VsRoot 'VC/Tools/MSVC'
    $Toolset = Get-ChildItem -LiteralPath $MsvcRoot -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName "bin/Host$HostArch/$TargetArch/cl.exe")) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "lib/$TargetArch/vcruntime.lib"))
    } | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
    if (-not $Toolset) { throw "Install MSVC $TargetArch C++ tools for Windows/$HostArch (including the target C runtime libraries)." }
    $ToolDir = Join-Path $Toolset.FullName "bin/Host$HostArch/$TargetArch"
    $Compiler = Join-Path $ToolDir 'cl.exe'
    $Linker = Join-Path $ToolDir 'link.exe'
    $Archiver = Join-Path $ToolDir 'lib.exe'
    foreach ($Tool in @($Compiler, $Linker, $Archiver)) {
        if (-not (Test-Path -LiteralPath $Tool)) { throw "The target MSVC toolchain is incomplete: $Tool." }
    }
    $SdkRoot = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10).KitsRoot10
    $Sdk = Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'Lib') -Directory | Where-Object {
        $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "ucrt/$TargetArch/ucrt.lib")) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName "um/$TargetArch/kernel32.lib"))
    } | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
    if (-not $Sdk) { throw "Install a Windows SDK with the $TargetArch UCRT and Windows import libraries." }
    $SdkInclude = Join-Path $SdkRoot "Include/$($Sdk.Name)"
    $IncludeDirs = @((Join-Path $Toolset.FullName 'include'), (Join-Path $SdkInclude 'ucrt'), (Join-Path $SdkInclude 'shared'), (Join-Path $SdkInclude 'um'), (Join-Path $SdkInclude 'winrt'))
    foreach ($IncludeDir in $IncludeDirs) {
        if (-not (Test-Path -LiteralPath $IncludeDir)) { throw "The target Windows SDK headers are incomplete: $IncludeDir." }
    }
    $LibDirs = @((Join-Path $Toolset.FullName "lib/$TargetArch"), (Join-Path $Sdk.FullName "ucrt/$TargetArch"), (Join-Path $Sdk.FullName "um/$TargetArch"))
    $TargetKey = $Target.Replace('-', '_')
    $CargoConfigFile = Join-Path $StageDir 'cargo.toml'
    $CargoConfig = [System.Text.StringBuilder]::new()
    [void]$CargoConfig.AppendLine("[target.$Target]")
    [void]$CargoConfig.AppendLine("linker = $(ConvertTo-Json -InputObject $Linker -Compress)")
    $CompilerEnvironment = @{
        "CC_$TargetKey" = $Compiler
        "AR_$TargetKey" = $Archiver
        'INCLUDE' = $IncludeDirs -join ';'
        'LIB' = $LibDirs -join ';'
        'PATH' = "$ToolDir;$([Environment]::GetEnvironmentVariable('PATH'))"
    }
    foreach ($Entry in $CompilerEnvironment.GetEnumerator()) {
        $Json = ConvertTo-Json -InputObject $Entry.Value -Compress
        [void]$CargoConfig.AppendLine("`n[env.$($Entry.Key)]")
        [void]$CargoConfig.AppendLine("value = $Json")
        [void]$CargoConfig.AppendLine('force = true')
    }
    [System.IO.File]::WriteAllText($CargoConfigFile, $CargoConfig.ToString(), [System.Text.UTF8Encoding]::new($false))
    $Archive = Join-Path $WorkDir 'source.tar.gz'
    if (-not (Test-Path -LiteralPath $Archive) -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $SourceSha256) {
        $Download = Join-Path $StageDir 'source.tar.gz'
        Invoke-WebRequest -Uri "https://codeload.github.com/rustonbsd/iroh-ssh/tar.gz/$Revision" -OutFile $Download
        if ((Get-FileHash -LiteralPath $Download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $SourceSha256) { throw 'iroh-ssh source archive SHA256 mismatch.' }
        Move-Item -LiteralPath $Download -Destination $Archive -Force
    }
    & rustup toolchain install $Toolchain --profile minimal
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install the pinned Rust toolchain.' }
    & rustup target add --toolchain $Toolchain $Target
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install the Rust target.' }
    & $Tar -xzf $Archive -C $StageDir
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the verified iroh-ssh source archive.' }
    $SourceDir = Join-Path $WorkDir "iroh-ssh-$Revision"
    if (Test-Path -LiteralPath $SourceDir) { Remove-Item -LiteralPath $SourceDir -Recurse -Force }
    Move-Item -LiteralPath (Join-Path $StageDir "iroh-ssh-$Revision") -Destination $SourceDir
    & git -C $RootDir apply "--directory=artifacts/iroh-ssh/$Rid/iroh-ssh-$Revision" $Patch
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply the pinned iroh-ssh readiness patch.' }
    & cargo "+$Toolchain" --config $CargoConfigFile build --locked --release --target $Target --manifest-path (Join-Path $SourceDir 'Cargo.toml') --target-dir (Join-Path $WorkDir 'target')
    if ($LASTEXITCODE -ne 0) { throw 'Failed to build the pinned iroh-ssh source.' }
    $StageOutput = Join-Path $StageDir $File
    $StageLicense = Join-Path $StageDir 'LICENSE.txt'
    $StageThirdParty = Join-Path $StageDir 'THIRD-PARTY-LICENSES.txt'
    Copy-Item -LiteralPath (Join-Path $WorkDir "target/$Target/release/$File") -Destination $StageOutput
    $Pe = [System.IO.BinaryReader]::new([System.IO.File]::OpenRead($StageOutput))
    try {
        if ($Pe.ReadUInt16() -ne 0x5a4d) { throw 'The compiled component is not a Windows executable.' }
        [void]$Pe.BaseStream.Seek(0x3c, [System.IO.SeekOrigin]::Begin)
        $PeOffset = $Pe.ReadUInt32()
        [void]$Pe.BaseStream.Seek($PeOffset, [System.IO.SeekOrigin]::Begin)
        if ($Pe.ReadUInt32() -ne 0x4550) { throw 'The compiled component has no PE signature.' }
        $ExpectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } else { 0xaa64 }
        if ($Pe.ReadUInt16() -ne $ExpectedMachine) { throw "The compiled component architecture does not match $Rid." }
    } finally { $Pe.Dispose() }
    Copy-Item -LiteralPath (Join-Path $SourceDir 'LICENSE') -Destination $StageLicense
    Copy-Item -LiteralPath $Patch -Destination (Join-Path $StageDir 'READINESS.patch')
    $Metadata = & cargo "+$Toolchain" metadata --locked --filter-platform $Target --format-version 1 --manifest-path (Join-Path $SourceDir 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Failed to read the locked dependency license metadata.' }
    $Notices = [System.Text.StringBuilder]::new()
    [void]$Notices.AppendLine('Corresponding source for each version-locked Rust crate is available at the download URL below.')
    [void]$Notices.AppendLine('For MPL-2.0 components, including attohttpc, these links provide the corresponding source covered by that license.')
    [void]$Notices.AppendLine("iroh-ssh modifications: see READINESS.patch next to this file (SHA256 $PatchSha256).")
    foreach ($Package in ($Metadata | ConvertFrom-Json).packages) {
        [void]$Notices.AppendLine("`n=== $($Package.name) $($Package.version) ($($Package.license)) ===")
        if ($Package.name -eq 'iroh-ssh' -and -not $Package.source) {
            [void]$Notices.AppendLine("Corresponding source: https://github.com/rustonbsd/iroh-ssh/archive/$Revision.tar.gz")
        } elseif ($Package.source -like 'registry+*') {
            [void]$Notices.AppendLine("Corresponding source: https://crates.io/api/v1/crates/$($Package.name)/$($Package.version)/download")
        } else { throw "Unrecognized locked dependency source for $($Package.name): $($Package.source)." }
        $PackageDir = Split-Path -Parent $Package.manifest_path
        foreach ($LicenseFile in (Get-ChildItem -LiteralPath $PackageDir -File -Recurse -Depth 1 | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' })) {
            [void]$Notices.AppendLine("`n--- $($LicenseFile.Name) ---")
            [void]$Notices.AppendLine([System.IO.File]::ReadAllText($LicenseFile.FullName))
        }
    }
    [System.IO.File]::WriteAllText($StageThirdParty, $Notices.ToString(), [System.Text.UTF8Encoding]::new($false))
    $Hash = (Get-FileHash -LiteralPath $StageOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    $LicenseHash = (Get-FileHash -LiteralPath $StageLicense -Algorithm SHA256).Hash.ToLowerInvariant()
    $ThirdPartyHash = (Get-FileHash -LiteralPath $StageThirdParty -Algorithm SHA256).Hash.ToLowerInvariant()
    $StageStamp = Join-Path $StageDir 'build.sha256'
    Set-Content -LiteralPath $StageStamp -Value "v4 $Revision $PatchSha256 $Toolchain $Target $Hash $LicenseHash $ThirdPartyHash" -Encoding ascii
    foreach ($Generated in @($File, 'LICENSE.txt', 'THIRD-PARTY-LICENSES.txt', 'READINESS.patch')) {
        Move-Item -LiteralPath (Join-Path $StageDir $Generated) -Destination (Join-Path $WorkDir $Generated) -Force
    }
    Move-Item -LiteralPath $StageStamp -Destination $Stamp -Force
} finally {
    if ($StageDir -and (Test-Path -LiteralPath $StageDir)) { Remove-Item -LiteralPath $StageDir -Recurse -Force }
    if ($Acquired) { $Mutex.ReleaseMutex() }
    $Mutex.Dispose()
}
