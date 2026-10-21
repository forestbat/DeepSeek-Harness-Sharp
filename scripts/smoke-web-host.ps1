param(
    [Parameter(Mandatory = $true)][string]$WebPublishDir,
    [Parameter(Mandatory = $true)][string]$TuiPublishDir,
    [Parameter(Mandatory = $true)][string]$GuiPublishDir
)
$ErrorActionPreference = 'Stop'

# Inspect published dependency graphs and actual files, not project-source wiring.
foreach ($directory in @($TuiPublishDir, $GuiPublishDir)) {
    $directory = (Resolve-Path $directory).Path
    $forbidden = @(Get-ChildItem $directory -File -Filter 'Microsoft.AspNetCore*.dll') + @(Get-ChildItem $directory -File -Filter 'Dsh.WebBridge*.dll')
    if ($forbidden.Count -ne 0) { throw "Non-web publish contains web assemblies: $($forbidden.Name -join ', ')" }
    foreach ($graph in Get-ChildItem $directory -File | Where-Object { $_.Name.EndsWith('.runtimeconfig.json') -or $_.Name.EndsWith('.deps.json') }) {
        if ((Get-Content $graph.FullName -Raw) -match 'Microsoft\.AspNetCore|Dsh\.WebBridge') { throw "Non-web dependency graph contains WebHost dependencies: $($graph.Name)" }
    }
}
foreach ($directory in @($TuiPublishDir, $GuiPublishDir, $WebPublishDir)) {
    $files = @(Get-ChildItem $directory -Recurse -File)
    $bundledIroh = @($files | Where-Object { $_.Name -in @('iroh-ssh', 'iroh-ssh.exe') })
    if ($bundledIroh.Count -ne 0) { throw "Publish bundles optional Iroh command: $($bundledIroh.Name -join ', ')" }
    $bundledSqlite = @($files | Where-Object { $_.Name -match '^(?:lib)?(?:e_sqlite3|sqlite3|winsqlite3)(?:\.\d+)*\.(?:dll|so|dylib|a|lib)(?:\.\d+)*$' })
    if ($bundledSqlite.Count -ne 0) { throw "Publish bundles optional system SQLite libraries: $($bundledSqlite.Name -join ', ')" }
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host "$directory : $bytes bytes"
}

$webDirectory = (Resolve-Path $WebPublishDir).Path
$exe = Join-Path $webDirectory $(if ($IsWindows) { 'dsharp-web.exe' } else { 'dsharp-web' })
# Keep Unix-domain socket paths short; every process and credential belongs to this smoke run.
$homeDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ('.daemon-tests/w' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $homeDirectory | Out-Null
$oldHome = $env:DSH_HOME
$env:DSH_HOME = $homeDirectory
$hostCommand = $null
$webCommand = $null
$ptyCommand = $null
$client = $null
$socket = $null
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))

function Start-SmokeProcess([string]$Name, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    [PSCustomObject]@{
        Name = $Name
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
    }
}

function Send-SmokeRequest([int]$Id, [string]$Method, $Parameters = $null) {
    $request = @{ jsonrpc = $protocol.JsonRpc.Version; id = $Id; method = $Method }
    if ($null -ne $Parameters) { $request.params = $Parameters }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 10 -Compress))
    $socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $deadline.Token).GetAwaiter().GetResult() | Out-Null
}

function Read-SmokeMessage {
    $message = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(65536)
        do {
            $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $deadline.Token).GetAwaiter().GetResult()
            if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw 'Published WebSocket closed before the expected response' }
            $message.Write($buffer, 0, $received.Count)
        } while (-not $received.EndOfMessage)
        $reply = [Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
        if ($null -ne $reply.error) { throw "Published RPC failed: $($reply.error.message)" }
        return $reply
    } finally { $message.Dispose() }
}

try {
    $ptyCommand = Start-SmokeProcess -Name 'pty' -Arguments @('tui', 'daemon')
    $ptyEndpoint = Join-Path $homeDirectory $(if ($IsWindows) { 'run/dsh.port' } else { 'run/dsh.sock' })
    while (-not (Test-Path $ptyEndpoint)) {
        $deadline.Token.ThrowIfCancellationRequested()
        if ($ptyCommand.Process.HasExited) { throw 'Published WebHost could not run the shared PTY daemon command' }
        Start-Sleep -Milliseconds 50
    }
    $hostCommand = Start-SmokeProcess -Name 'host' -Arguments @('host', 'serve')
    $hostEndpoint = Join-Path $homeDirectory $(if ($IsWindows) { 'run/host.port' } else { 'run/host.sock' })
    while (-not (Test-Path $hostEndpoint)) {
        $deadline.Token.ThrowIfCancellationRequested()
        if ($hostCommand.Process.HasExited) { throw 'Published WebHost could not serve the shared Host command' }
        Start-Sleep -Milliseconds 50
    }
    $webCommand = Start-SmokeProcess -Name 'web' -Arguments @('--port', '0')
    $portFile = Join-Path $homeDirectory 'run/web.port'
    while (-not (Test-Path $portFile)) {
        $deadline.Token.ThrowIfCancellationRequested()
        if ($webCommand.Process.HasExited) { throw 'Published WebHost exited before announcing its endpoint' }
        Start-Sleep -Milliseconds 50
    }
    $port = (Get-Content $portFile -Raw).Trim()
    $origin = "http://127.0.0.1:$port"
    $token = (Get-Content (Join-Path $homeDirectory 'run/web.token') -Raw).Trim()
    $cookies = [Net.CookieContainer]::new()
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.CookieContainer = $cookies
    $client = [Net.Http.HttpClient]::new($handler)
    $unauthenticated = $client.GetAsync("$origin/api/session", $deadline.Token).GetAwaiter().GetResult()
    if ([int]$unauthenticated.StatusCode -ne 401) { throw 'Anonymous session endpoint did not refuse access' }
    $unauthenticated.Dispose()
    $protocolResponse = $client.GetAsync("$origin/protocol.js", $deadline.Token).GetAwaiter().GetResult()
    try {
        if ([int]$protocolResponse.StatusCode -ne 200 -or $protocolResponse.Content.Headers.ContentType.MediaType -ne 'text/javascript') { throw 'Published public protocol resource is missing' }
        $protocolScript = $protocolResponse.Content.ReadAsStringAsync($deadline.Token).GetAwaiter().GetResult()
        if ($protocolScript.Contains($token)) { throw 'Published protocol resource contains credentials' }
        $protocol = @{}
        foreach ($line in $protocolScript.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($line -notmatch '^export const (?<name>[A-Za-z]+) = Object\.freeze\((?<json>.+)\);$') { throw 'Published protocol module has an invalid export' }
            $protocol[$Matches.name] = $Matches.json | ConvertFrom-Json -AsHashtable
        }
        foreach ($name in @('SessionStatus', 'SessionKind', 'Capability', 'AgentMode', 'HostProtocol', 'AgentProtocol', 'PtyProtocol', 'FilesProtocol', 'GitProtocol', 'NotificationsProtocol', 'BridgeProtocol', 'PendingKind', 'MachineConnectionState', 'NotificationKind', 'NotificationView', 'GitDiffSource', 'SessionEventType', 'StreamChunkType', 'ContentBlockType', 'SessionEventSource', 'TurnEndKind', 'JsonRpc')) {
            if (-not $protocol.ContainsKey($name) -or $protocol[$name].Count -eq 0) { throw "Published protocol metadata missing: $name" }
        }
    } finally { $protocolResponse.Dispose() }
    $foreign = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$origin/api/login")
    $foreign.Headers.Add('Origin', 'http://foreign.invalid')
    $foreign.Content = [Net.Http.StringContent]::new("token=$token", [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $refused = $client.SendAsync($foreign, $deadline.Token).GetAwaiter().GetResult()
    if ([int]$refused.StatusCode -ne 403) { throw 'Cross-origin login was not refused' }
    $refused.Dispose(); $foreign.Dispose()
    $login = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$origin/api/login")
    $login.Headers.Add('Origin', $origin)
    $login.Content = [Net.Http.StringContent]::new("token=$token", [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $accepted = $client.SendAsync($login, $deadline.Token).GetAwaiter().GetResult()
    if ([int]$accepted.StatusCode -ne 204) { throw 'Token login failed in published WebHost' }
    $cookie = $accepted.Headers.GetValues('Set-Cookie') -join ';'
    if ($cookie -notmatch '(?i)httponly' -or $cookie -notmatch '(?i)samesite=strict' -or $cookie.Contains($token)) { throw 'Published cookie boundary is invalid' }
    $accepted.Dispose(); $login.Dispose()
    $page = $client.GetAsync("$origin/", $deadline.Token).GetAwaiter().GetResult()
    if ([int]$page.StatusCode -ne 200 -or $page.Content.Headers.ContentType.MediaType -ne 'text/html') { throw 'Published WebHost did not serve its HTML entry point' }
    if (($page.Headers.GetValues('Content-Security-Policy') -join ';') -notmatch "frame-ancestors 'none'") { throw 'Published CSP is missing' }
    $page.Dispose()
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.Cookies = $cookies
    $socket.Options.SetRequestHeader('Origin', $origin)
    $socket.ConnectAsync([Uri]"ws://127.0.0.1:$port/ws", $deadline.Token).GetAwaiter().GetResult()
    do { $reply = Read-SmokeMessage } while ($reply.method -ne $protocol.BridgeProtocol.NotificationView -or $reply.params.State -ne $protocol.MachineConnectionState.Connected)
    Send-SmokeRequest 1 $protocol.HostProtocol.MethodInfo
    do { $reply = Read-SmokeMessage } while ($reply.id -ne 1)
    if ([string]::IsNullOrWhiteSpace($reply.result.HostVersion)) { throw 'Published Fleet forwarding returned no Host info' }
    $shell = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/cmd.exe' } else { '/bin/sh' }
    $arguments = @()
    if ($IsWindows) { $arguments = @('/d', '/q') }
    Send-SmokeRequest 2 $protocol.AgentProtocol.MethodAgentStart @{
        AgentKind = 'terminal'; ProjectPath = $homeDirectory; Title = 'Published terminal'
        Command = @{ Executable = $shell; Arguments = @($arguments) }
    }
    do { $reply = Read-SmokeMessage } while ($reply.id -ne 2)
    $ptyId = $reply.result.SessionId
    if ([string]::IsNullOrWhiteSpace($ptyId)) { throw 'Published Host did not register the terminal' }
    Send-SmokeRequest 3 $protocol.PtyProtocol.MethodPtyAttach @{ PtyId = $ptyId; ViewId = 'published' }
    do { $reply = Read-SmokeMessage } while ($reply.id -ne 3)
    $input = if ($IsWindows) { "set /a p=6*7 >nul`r`necho PUBLISHED-%p%`r`n" } else { 'printf ''PUBLISHED-%s\n'' "$((6*7))"' + "`r" }
    Send-SmokeRequest 4 $protocol.PtyProtocol.MethodPtyWrite @{ PtyId = $ptyId; Base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($input)) }
    $output = [Text.StringBuilder]::new()
    $written = $false
    do {
        $reply = Read-SmokeMessage
        if ($reply.id -eq 4) { $written = $true }
        if ($reply.method -eq $protocol.PtyProtocol.NotificationPtyOutput -and $reply.params.PtyId -eq $ptyId) {
            $output.Append([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($reply.params.Base64))) | Out-Null
        }
    } while (-not $written -or -not $output.ToString().Contains('PUBLISHED-42'))
    Send-SmokeRequest 5 $protocol.PtyProtocol.MethodPtyStop @{ PtyId = $ptyId }
    do { $reply = Read-SmokeMessage } while ($reply.id -ne 5)
    if ($reply.result -ne $true) { throw 'Published Host did not stop the terminal' }
    $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', $deadline.Token).GetAwaiter().GetResult()
    Write-Host 'Published WebHost: shared Host/PTY commands, HTTP authentication/CSRF/CSP/assets, Fleet forwarding and real terminal input/output/stop passed.'
} finally {
    if ($socket) { $socket.Dispose() }
    if ($client) { $client.Dispose() }
    foreach ($command in @($webCommand, $hostCommand, $ptyCommand)) {
        if (-not $command) { continue }
        try {
            if (-not $command.Process.HasExited) { $command.Process.Kill($true) }
            $command.Process.WaitForExit()
            [IO.File]::WriteAllText((Join-Path $homeDirectory "$($command.Name).out"), $command.Output.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $homeDirectory "$($command.Name).err"), $command.Error.GetAwaiter().GetResult())
        } finally { $command.Process.Dispose() }
    }
    $deadline.Dispose()
    $env:DSH_HOME = $oldHome
    foreach ($name in @('web.err', 'host.err', 'pty.err')) {
        $log = Join-Path $homeDirectory $name
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) { Get-Content $log | Write-Host }
    }
    Remove-Item $homeDirectory -Recurse -Force
}
