# Temporary Discussion64 observer; remove this script and its workflow call after diagnosis.
# This is not a clean performance run: EventPipe sampling/rundown and tool setup add overhead.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$privateDirectory = Join-Path $root ('artifacts/owned-mcp-observer-' + [Guid]::NewGuid().ToString('N'))
$consumer = $null
$consumerExit = 1
$observerReady = $false

try {
    # Both diagnostic streams stay private; retain at most 512 KiB of stdout and discard stderr while draining it.
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class OwnedMcpObservation29
{
    public string Status = "unavailable";
    public int ExitCode = -1;
    public long ElapsedMilliseconds;
    public bool Truncated;
    public string Text = "";

    private static async Task Drain(Stream stream, MemoryStream retained, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return;
            if (retained != null)
            {
                int keep = Math.Min(count, 512 * 1024 - (int)retained.Length);
                retained.Write(buffer, 0, keep);
            }
        }
    }

    public static async Task<OwnedMcpObservation29> Run(string executable, string[] arguments,
        string workingDirectory, string temporaryDirectory, int milliseconds)
    {
        var result = new OwnedMcpObservation29();
        var clock = Stopwatch.StartNew();
        using var process = new Process();
        using var retained = new MemoryStream();
        using var reads = new CancellationTokenSource();
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        process.StartInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        // Tool-only temporary paths: never change the consumer's environment or configuration.
        process.StartInfo.Environment["TEMP"] = temporaryDirectory;
        process.StartInfo.Environment["TMP"] = temporaryDirectory;
        try
        {
            process.Start();
            output = Drain(process.StandardOutput.BaseStream, retained, reads.Token);
            error = Drain(process.StandardError.BaseStream, null, reads.Token);
            using var deadline = new CancellationTokenSource(milliseconds);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // Also bound pipe EOF; a successful process exit must not allow an unbounded reader wait.
            await Task.WhenAll(output, error).WaitAsync(deadline.Token).ConfigureAwait(false);
            result.ExitCode = process.ExitCode;
            result.Status = result.ExitCode == 0 ? "ok" : "tool-failed";
        }
        catch (OperationCanceledException) { result.Status = "observer-deadline"; }
        catch { result.Status = "tool-unavailable"; }
        finally
        {
            // Kill only the diagnostic tool tree created above, never the sibling consumer or a discovered process.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(1000)) result.Status = "observer-cleanup-incomplete";
                }
            }
            catch { result.Status = "observer-cleanup-incomplete"; }
            reads.Cancel();
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
            result.Truncated = retained.Length == 512 * 1024;
            // Never use partially collected output to infer a phase after a tool error/deadline.
            if (result.Status == "ok") result.Text = Encoding.UTF8.GetString(retained.ToArray());
            result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
        }
        return result;
    }
}
'@ -ErrorAction Stop
    if (-not $IsWindows) { throw 'Windows-only diagnostic observer' }
    [IO.Directory]::CreateDirectory($privateDirectory) | Out-Null
    $tools = Join-Path $privateDirectory 'tools'
    $install = [OwnedMcpObservation29]::Run('dotnet', @(
        'tool', 'install', 'dotnet-stack', '--version', '10.0.750501', '--tool-path', $tools
    ), $root, $privateDirectory, 60000).GetAwaiter().GetResult()
    Write-Host "MCP_OBSERVER setup=$($install.Status) exit=$($install.ExitCode) elapsed_ms=$($install.ElapsedMilliseconds)"
    $stack = Join-Path $tools 'dotnet-stack.exe'
    $observerReady = $install.Status -eq 'ok' -and [IO.File]::Exists($stack)
    $install = $null
} catch {
    # Exception text may contain absolute paths, package sources or account data.
    Write-Host 'MCP_OBSERVER setup=unavailable'
}

# Match only these literal, source-verified declaring types/methods; never print a captured frame or argument.
# Async state-machine frames use <Method> in the name; their absence is NOT evidence of phase completion.
$methods = @(
    @('Dsh.Sandbox.WindowsDirectoryCollector', 'Collect'),
    @('Dsh.Sandbox.WindowsAclLease', 'AddTree'),
    @('Dsh.Sandbox.WindowsAclLease', 'Apply'),
    @('Dsh.Sandbox.WindowsAclLease', 'ApplyGrants'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'DemandAmbientAncestorGrant'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'Open'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'CanReuseReadGrant'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'ReadGrantAcl'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'Grant'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'GrantCore'),
    @('Dsh.Sandbox.WindowsAclLease+Entry', 'RemoveCore'),
    @('Dsh.Sandbox.WindowsAclLease', 'Dispose'),
    @('Dsh.Sandbox.WindowsSandbox', 'Start'),
    @('Dsh.Sandbox.WindowsSandbox', 'DisposeAsync'),
    @('Dsh.Mcp.OwnedStdioClientTransport', 'ConnectAsync'),
    @('Dsh.Mcp.OwnedStdioClientTransport', 'DisposeAsync'),
    @('Dsh.Mcp.McpService', 'ConnectOneAsync'),
    @('Dsh.Mcp.McpService', 'DisconnectAllAsync'),
    @('Dsh.Mcp.McpServerHandle', 'ConnectAsync'),
    @('Dsh.Mcp.McpServerHandle', 'CallOwnedToolAsync'),
    @('Dsh.Mcp.McpServerHandle', 'CloseAsync'),
    @('Dsh.Mcp.McpToolBridge', 'ExecuteAsync')
)
$frameworkMethods = @(
    @('System.IO.Enumeration.FileSystemEnumerator', 'Init'),
    @('System.IO.Enumeration.FileSystemEnumerator', 'CreateDirectoryHandle'),
    @('System.IO.Enumeration.FileSystemEnumerator', 'GetData'),
    @('System.IO.Enumeration.FileSystemEnumerator', 'MoveNext'),
    @('System.Collections.Concurrent.BlockingCollection', 'TryTakeWithNoTimeValidation'),
    @('System.Threading.ManualResetEventSlim', 'Wait'),
    @('System.Threading.SemaphoreSlim', 'Wait'),
    @('System.Threading.Thread', 'Join')
)
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests.exe'))
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    # All three streams are inherited exactly as for the original standalone invocation.
    foreach ($argument in @(
        '-noColor', '-parallelMode', 'none', '-reporter', 'verbose',
        '-method', 'Dsh.Tests.Mcp.McpSettingsBoundaryTests.OwnedStdio_PreservesToolOutput_ButNeverMutatesSettings',
        '-trait-', 'Category=GpuStress', '-trait-', 'Category=OnlyGpu',
        '-trait-', 'Category=RenderBench', '-trait-', 'Category=LlmSmoke', '-failSkips'
    )) { $start.ArgumentList.Add($argument) }
    $consumer = [Diagnostics.Process]::Start($start)
    # Force acquisition of the Windows process handle before handing its ID to the observer.
    $null = $consumer.Handle
    $started = $consumer.StartTime.ToUniversalTime().Ticks
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Host 'MCP_OBSERVER mode=temporary-overhead-not-performance-proof target=owned-root-only'
    if ($observerReady) {
        try {
            foreach ($atSeconds in @(8, 26, 44)) {
                while (-not $consumer.HasExited -and $clock.Elapsed.TotalSeconds -lt $atSeconds) {
                    Start-Sleep -Milliseconds 100
                }
                if ($consumer.HasExited) { break }
                # Never attach by name/listing or after the original pre-cutoff observation window.
                if ($clock.Elapsed.TotalSeconds -ge 46 -or $consumer.StartTime.ToUniversalTime().Ticks -ne $started) {
                    Write-Host 'MCP_OBSERVER sample=skipped-window-or-identity'
                    break
                }
                $at = $clock.ElapsedMilliseconds
                $sample = [OwnedMcpObservation29]::Run($stack, @('report', '--process-id', [string]$consumer.Id),
                    $root, $privateDirectory, 8000).GetAwaiter().GetResult()
                Write-Host "MCP_OBSERVER sample=$($sample.Status) start_ms=$at elapsed_ms=$($sample.ElapsedMilliseconds) exit=$($sample.ExitCode) truncated=$([int]$sample.Truncated)"
                Write-Host "MCP_OBSERVER root_cpu_ms=$([long]$consumer.TotalProcessorTime.TotalMilliseconds)"
                if ($sample.Status -eq 'ok') {
                    $lines = $sample.Text -split '\r?\n'
                    $threads = @($lines | Where-Object { $_ -match '^Thread \(0x[0-9a-fA-F]+\):$' }).Count
                    $frames = @($lines | Where-Object { $_ -match '^\s+[^!]+!' }).Count
                    $matched = 0
                    foreach ($method in $methods) {
                        # + and / both occur as nested-type separators in diagnostic formatters.
                        $type = [regex]::Escape($method[0]).Replace('\+', '[+/]')
                        $name = [regex]::Escape($method[1])
                        $pattern = '!' + $type + '(?:\.' + $name + '\(|[+/]<'+ $name + '>[^\s]*\.MoveNext\(|(?:[+/][^\s.]+)?\.<' + $name + '>g__)'
                        $count = @($lines | Where-Object { $_ -match $pattern }).Count
                        if ($count -gt 0) {
                            $matched += $count
                            Write-Host "MCP_OBSERVER method=$($method[0]).$($method[1]) frames=$count"
                        }
                    }
                    foreach ($method in $frameworkMethods) {
                        $type = [regex]::Escape($method[0])
                        $name = [regex]::Escape($method[1])
                        $pattern = '!' + $type + '(?:`[0-9]+(?:\[[^\r\n]*\])?)?\.' + $name + '\('
                        $count = @($lines | Where-Object { $_ -match $pattern }).Count
                        if ($count -gt 0) {
                            Write-Host "MCP_OBSERVER framework_method=$($method[0]).$($method[1]) frames=$count"
                        }
                    }
                    Write-Host "MCP_OBSERVER threads=$threads frames=$frames allowlisted_frames=$matched"
                }
                $sample = $null
                $lines = $null
                if (-not $consumer.HasExited -and $clock.Elapsed.TotalSeconds -ge 56) { break }
            }
        } catch {
            Write-Host 'MCP_OBSERVER sample=unavailable'
        }
    }
    # The original Fact keeps its 60000ms timeout; observer failures do not replace/skip/retry it.
    $consumer.WaitForExit()
    $consumerExit = $consumer.ExitCode
} catch {
    Write-Host 'MCP_OBSERVER consumer-launch-or-wait=failed'
} finally {
    if ($null -ne $consumer) {
        # Even an observer exception must preserve the actual test result and let its own cleanup finish.
        try { $consumer.WaitForExit(); $consumerExit = $consumer.ExitCode } catch { }
        $consumer.Dispose()
    }
    try {
        if ([IO.Directory]::Exists($privateDirectory)) { [IO.Directory]::Delete($privateDirectory, $true) }
        Write-Host 'MCP_OBSERVER private-tool-and-trace-cleanup=ok'
    } catch {
        Write-Host 'MCP_OBSERVER private-tool-and-trace-cleanup=failed'
    }
}
exit $consumerExit
