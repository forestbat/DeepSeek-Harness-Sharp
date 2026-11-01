$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-BoundedPrivateCommand([string] $Executable, [string[]] $Arguments, [int] $Seconds = 60) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw 'The exact Windows observer command did not terminate.' }
            throw 'The owned Windows observer command exceeded its deadline.'
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.GetAwaiter().GetResult(); ErrorOutput = $errorOutput.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

if (-not $IsWindows) { throw 'This resource observer requires Windows.' }
if (Get-Process -Name Procmon, Procmon64, Procmon64a -ErrorAction SilentlyContinue) {
    throw 'Refusing to share or terminate an existing Process Monitor capture.'
}
$root = Join-Path (Get-Location) ".artifacts/windows-native-observer-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $root
$procmon = Join-Path $root 'Procmon64.exe'
$capture = $null
try {
    $archive = Join-Path $root 'ProcessMonitor.zip'
    Invoke-WebRequest 'https://download.sysinternals.com/files/ProcessMonitor.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $root
    $signature = Get-AuthenticodeSignature -LiteralPath $procmon
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw 'The native observer must have a valid Microsoft signature.'
    }
    $pml = Join-Path $root 'capture.pml'
    $xml = Join-Path $root 'capture.xml'
    $info = [System.Diagnostics.ProcessStartInfo]::new($procmon)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in @('/AcceptEula', '/Quiet', '/Minimized', '/BackingFile', $pml)) { $info.ArgumentList.Add($argument) }
    $capture = [System.Diagnostics.Process]::Start($info)
    try {
        $idle = Invoke-BoundedPrivateCommand $procmon @('/WaitForIdle') 15
        if ($idle.ExitCode -ne 0) { throw 'Process Monitor did not acknowledge capture readiness.' }
        $testHost = Join-Path (Get-Location) '.artifacts/sandbox-native-publish/Dsh.Tests.exe'
        $test = Invoke-BoundedPrivateCommand $testHost @('-noColor', '-parallelMode', 'none', '-failSkips', '-reporter', 'verbose', '-method', 'Dsh.Tests.WindowsSandboxAuthorityTests.WritableDirectory_IsRefusedBeforePayloadAndAclChanges_AndReadonlyLeaseStillWorks') 60
    }
    finally {
        $stop = Invoke-BoundedPrivateCommand $procmon @('/Terminate', '/Quiet') 15
        if ($stop.ExitCode -ne 0) { throw 'Stopping the owned native resource capture failed.' }
    }
    $childIds = @([regex]::Matches($test.Output + $test.ErrorOutput, 'Windows readonly control PID ([0-9]+)') | ForEach-Object { [int] $_.Groups[1].Value } | Sort-Object -Unique)
    Write-Output "NATIVE_OBSERVER_CONSUMER_EXIT=$($test.ExitCode) OWNED_CHILD_COUNT=$($childIds.Count)"
    if ($childIds.Count -eq 0) { throw 'No failing owned child PID was reported; the trace cannot establish this failure.' }
    $export = Invoke-BoundedPrivateCommand $procmon @('/AcceptEula', '/Quiet', '/OpenLog', $pml, '/SaveAs1', $xml) 60
    if ($export.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $xml)) { throw 'Exporting the owned native resource capture failed.' }
    $redactions = @{
        ([Environment]::GetFolderPath('UserProfile')) = '[profile]'
        ((Get-Location).Path) = '[repo]'
        ([Environment]::GetFolderPath('Windows')) = '[system]'
        ([Environment]::GetFolderPath('ProgramFiles')) = '[program-files]'
    }
    $events = [System.Collections.Generic.Queue[object]]::new()
    $reader = [System.Xml.XmlReader]::Create($xml, [System.Xml.XmlReaderSettings]@{ DtdProcessing = 'Prohibit'; XmlResolver = $null })
    try {
        while ($reader.ReadToFollowing('event')) {
            $subtree = $reader.ReadSubtree()
            try { $node = [System.Xml.XmlDocument]::new(); $node.Load($subtree) }
            finally { $subtree.Dispose() }
            $fields = @{}
            foreach ($field in $node.DocumentElement.ChildNodes) { $fields[($field.Name -replace '_', '').ToLowerInvariant()] = $field.InnerText }
            if (-not $fields.ContainsKey('pid') -or [int] $fields['pid'] -notin $childIds) { continue }
            $resource = [string] $fields['path']
            foreach ($prefix in $redactions.Keys) {
                if ($prefix.Length -gt 0) { $resource = $resource.Replace($prefix, $redactions[$prefix], [StringComparison]::OrdinalIgnoreCase) }
            }
            $resource = $resource -replace 'S-1-5-21(?:-[0-9]+)+', '[user-sid]'
            $resource = $resource -replace 'S-1-15-2(?:-[0-9]+)+', '[container-sid]'
            $access = [regex]::Match([string] $fields['detail'], 'Desired Access: ([^,]+)').Groups[1].Value
            $status = [regex]::Match([string] $fields['detail'], 'Exit Status: ([^,]+)').Groups[1].Value
            $frames = @($node.SelectNodes('//*[local-name()="frame"]') | Select-Object -First 8 | ForEach-Object {
                $pathNode = $_.SelectSingleNode('./*[local-name()="path"]')
                $locationNode = $_.SelectSingleNode('./*[local-name()="location"]')
                $module = if ($pathNode) { [IO.Path]::GetFileName($pathNode.InnerText) } else { '' }
                $offset = if ($locationNode) { [regex]::Match($locationNode.InnerText, '(?:\+\s*)?0x[0-9a-fA-F]+').Value } else { '' }
                "$module$offset"
            })
            $events.Enqueue([ordered]@{ childPid = [int] $fields['pid']; operation = $fields['operation']; resource = $resource; result = $fields['result']; desiredAccess = $access; exitStatus = $status; moduleFrames = $frames })
            if ($events.Count -gt 256) { $null = $events.Dequeue() }
        }
    }
    finally { $reader.Dispose() }
    if ($events.Count -eq 0) { throw 'The resource trace contained no exact-PID child events.' }
    foreach ($event in $events) { Write-Output ($event | ConvertTo-Json -Compress -Depth 4) }
}
finally {
    if ($capture) {
        try {
            if (-not $capture.HasExited) {
                $capture.Kill($true)
                if (-not $capture.WaitForExit(10000)) { throw 'The exact native observer process did not terminate.' }
            }
        }
        finally { $capture.Dispose() }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
