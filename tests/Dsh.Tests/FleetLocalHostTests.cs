using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dsh.Fleet;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.AgentAdapters;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * Dsh.Fleet and the fleet CLI against real `dsharp host serve` processes of this build, each under its own throwaway harness home.
 * Covers starting the local host on demand, reconnecting after the host process is killed, and `dsharp machine|session|doctor` end to end on two machines.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class FleetLocalHostTests(ITestOutputHelper output)
{
    private static readonly TimeSpan RecoveryArrival = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeArrival = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** A negative peer accepts connections but never responds to the Host handshake. */
    [Fact(Explicit = true)]
    public async Task Cli_StartsAHostWhenASilentPeerOwnsTheEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var home = RealHostProcesses.CreateHome("fp");
        using var listener = new Socket(OperatingSystem.IsWindows() ? AddressFamily.InterNetwork : AddressFamily.Unix,
            SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var token = LocalEndpointToken.Issue(RemoteHostEndpoint.EndpointFile(home));
            if (OperatingSystem.IsWindows())
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                await File.WriteAllTextAsync(RemoteHostEndpoint.PortFile(home),
                    ((IPEndPoint)listener.LocalEndPoint!).Port.ToString(), cancellationToken);
            }
            else
                listener.Bind(new UnixDomainSocketEndPoint(RemoteHostEndpoint.SocketPath(home)));

            listener.Listen(1);
            var accepting = listener.AcceptAsync(cancellationToken).AsTask();

            var listing = FleetCliProcess.RunAsync(RealHostProcesses.Executable, home, "session", "list");
            using var peer = await accepting.WaitAsync(ProbeArrival, cancellationToken);
            var result = await listing.WaitAsync(RecoveryArrival, cancellationToken);
            Assert.True(result.ExitCode == 0, result.Error);
            await using var stream = await RemoteHostConnector.ConnectAsync(home, cancellationToken);
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, cancellationToken);
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            listener.Dispose();
            TempTree.Delete(home);
        }
    }

    [Theory(Explicit = true)]
    [InlineData(AgentKinds.Terminal)]
    [InlineData(AgentKinds.OpenCode)]
    public async Task Cli_StopsExternalAndAcpAgentsThroughHostAgentStop(string kind)
    {
        if (kind == AgentKinds.OpenCode && AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fs");
        try
        {
            var started = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.True(started.ExitCode == 0, started.Error);
            await using var connection = new MachineConnection(MachineSpec.Local(home), FleetConnectionTests.TestOptions);
            connection.Start();
            var client = (await connection.WaitConnectedAsync(TestContext.Current.CancellationToken)).Client;
            var session = await client.Agent.StartAgentAsync(new RemoteAgentStartRequest(kind, home,
                Mode: kind == AgentKinds.OpenCode ? RemoteAgentModes.Acp : RemoteAgentModes.Terminal,
                Command: kind == AgentKinds.Terminal ? new RemoteAgentCommand(TestShell.FileName, TestShell.Arguments) : null), Ct);
            Assert.Equal(kind == AgentKinds.OpenCode ? HostSessionKinds.Acp : HostSessionKinds.External, session.Kind);

            var stopped = await FleetCliProcess.RunAsync(executable, home, "session", "stop", $"local/{session.SessionId}");
            Assert.True(stopped.ExitCode == 0, stopped.Error);
            await WaitForSessionStatusAsync(home, session.SessionId, HostSessionStatuses.Exited);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }
    [Fact(Explicit = true)]
    public async Task LocalHost_IsStartedOnDemand_AndTheConnectionRecoversAfterTheHostIsKilled()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fl");
        try
        {
            Process? host = null;
            var starts = 0;
            var hostOutput = new ConcurrentQueue<string>();
            var options = FleetConnectionTests.TestOptions with
            {
                StartLocalHost = (_, _) =>
                {
                    Interlocked.Increment(ref starts);
                    host = RealHostProcesses.StartHost(executable, home, hostOutput.Enqueue);
                    return Task.CompletedTask;
                },
            };
            await using var connection = new MachineConnection(MachineSpec.Local(home, "real"), options);
            connection.Start();
            await connection.WaitSettledAsync(TestContext.Current.CancellationToken);
            Assert.Equal(MachineConnectionState.Connected, connection.State);
            var first = Assert.IsType<Process>(host);
            Assert.Equal(HostProtocol.ReleaseVersion, connection.Info!.HostVersion);
            await using (var healthy = new MachineConnection(MachineSpec.Local(home), options))
            {
                healthy.Start();
                await healthy.WaitConnectedAsync(Ct).WaitAsync(RecoveryArrival, Ct);
                Assert.Equal(1, starts);
                Assert.Same(first, host);
            }

            var projected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.SessionsChanged += changed =>
            {
                if (changed.Sessions.Any(session => session.Kind == HostSessionKinds.Terminal))
                    projected.TrySetResult();
            };
            var ptyId = await StartShellAsync(connection, home);
            await projected.Task.WaitAsync(RecoveryArrival, Ct);
            Assert.Contains(connection.Sessions, session => session.Kind == HostSessionKinds.Terminal && session.SessionId == ptyId);

            var previousLease = await connection.WaitConnectedAsync(Ct);
            var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leaseEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = previousLease.Lifetime.Register(() => leaseEnded.TrySetResult());
            var firstExited = first.WaitForExitAsync(Ct);
            var states = new ConcurrentQueue<string>();
            // Subscribe before the kill, and publish both transitions through synchronized barriers.
            connection.StateChanged += changed =>
            {
                states.Enqueue($"{changed.State}: {changed.Detail}");
                if (changed.State == MachineConnectionState.Reconnecting)
                    reconnecting.TrySetResult();
                else if (changed.State == MachineConnectionState.Connected && reconnecting.Task.IsCompleted)
                    recovered.TrySetResult();
            };
            first.Kill();
            try
            {
                await Task.WhenAll(firstExited, leaseEnded.Task, recovered.Task).WaitAsync(RecoveryArrival, Ct);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
                var diagnostic = $"Host recovery failed; connection {connection.State} ({connection.Detail}); "
                    + $"previous lease cancelled={previousLease.Lifetime.IsCancellationRequested}; "
                    + $"first process {first.Id} exited={first.HasExited}; "
                    + $"latest process {host?.Id} exited={host?.HasExited}; "
                    + $"states: {string.Join(" -> ", states)}; host output:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, hostOutput);
                diagnostic = diagnostic.Replace(home, "<test-home>", StringComparison.OrdinalIgnoreCase);
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (userHome.Length > 0)
                    diagnostic = diagnostic.Replace(userHome, "<user-home>", StringComparison.OrdinalIgnoreCase);
                Assert.Fail(diagnostic);
            }
            Assert.True(previousLease.Lifetime.IsCancellationRequested);
            Assert.True(first.HasExited);
            Assert.NotSame(first, host);
            Assert.False(Assert.IsType<Process>(host).HasExited);
            Assert.Equal(2, starts);
            Assert.Equal(MachineConnectionState.Connected, connection.State);
            Assert.Contains(connection.Sessions, session => session.SessionId == ptyId);
            var replacementLease = await connection.WaitConnectedAsync(Ct);
            Assert.Contains(await replacementLease.Client.ListHostSessionsAsync(Ct), session => session.SessionId == ptyId);
            var listed = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.True(listed.ExitCode == 0, listed.Error);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is ["local", var id, HostSessionKinds.Terminal, ..] && id == ptyId);
            Assert.Equal(2, starts);
            output.WriteLine($"RECOVERY first={first.Id} exited={first.HasExited} leaseCancelled={previousLease.Lifetime.IsCancellationRequested} replacement={host!.Id} exited={host.HasExited} starts={starts} pty={ptyId}");
            output.WriteLine($"STATES {string.Join(" -> ", states)}");
            output.WriteLine($"CLI exit={listed.ExitCode}{Environment.NewLine}{listed.Output}");
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    /** Two machines (this harness home and a second local one), each with a real host the CLI starts on demand. */
    [Fact(Explicit = true)]
    public async Task Cli_ManagesMachines_ListsStopsAndDiagnosesSessions()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fc");
        var second = RealHostProcesses.CreateHome("fd");
        try
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var added = await FleetCliProcess.RunAsync(executable, home, "machine", "add", "second", "--local", "--home", second);
            Assert.Equal(0, added.ExitCode);
            var machines = await FleetCliProcess.RunAsync(executable, home, "machine", "list");
            Assert.Contains(FleetCliProcess.Rows(machines.Output), row => row is ["second", MachineKinds.Local, var target, ..] && target == second);

            var empty = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.True(empty.ExitCode == 0, empty.Error);
            var localShell = await StartShellAsync(home);
            var secondShell = await StartShellAsync(second);

            var listed = await FleetCliProcess.RunAsync(executable, home, "session", "list");
            Assert.Equal(0, listed.ExitCode);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is ["local", var id, HostSessionKinds.Terminal, ..] && id == localShell);
            Assert.Contains(FleetCliProcess.Rows(listed.Output), row => row is ["second", var id, HostSessionKinds.Terminal, ..] && id == secondShell);
            var narrowed = await FleetCliProcess.RunAsync(executable, home, "session", "list", "--machine", "second");
            Assert.DoesNotContain(localShell, narrowed.Output, StringComparison.Ordinal);

            var doctor = await FleetCliProcess.RunAsync(executable, home, "doctor", "--machine", "second");
            Assert.True(doctor.ExitCode == 0, doctor.Output);
            Assert.Contains("[ok] connection:", doctor.Output, StringComparison.Ordinal);
            // The endpoint checks read the POSIX status script's output, which FleetDoctor does not run for a local Windows machine.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains("[ok] host:", doctor.Output, StringComparison.Ordinal);
                Assert.Contains("[ok] permissions:", doctor.Output, StringComparison.Ordinal);
                Assert.Contains("[ok] pty daemon:", doctor.Output, StringComparison.Ordinal);
            }

            var stopped = await FleetCliProcess.RunAsync(executable, home, "session", "stop", $"second/{secondShell}");
            Assert.True(stopped.ExitCode == 0, stopped.Error);
            await WaitForSessionStatusAsync(second, secondShell, HostSessionStatuses.Exited);
            var missing = await FleetCliProcess.RunAsync(executable, home, "session", "stop", "second/no-such-session");
            Assert.Equal(1, missing.ExitCode);
            Assert.True(missing.Error.Contains("no session", StringComparison.Ordinal), missing.Error);

            Assert.Equal(0, (await FleetCliProcess.RunAsync(executable, home, "machine", "remove", "second")).ExitCode);
            Assert.DoesNotContain(secondShell, (await FleetCliProcess.RunAsync(executable, home, "session", "list")).Output, StringComparison.Ordinal);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            RealHostProcesses.KillAll(second);
            TempTree.Delete(home);
            TempTree.Delete(second);
        }
    }

    /** `dsharp session attach` in a real terminal: keystrokes reach the host's shell, its output comes back, and Ctrl+] leaves the session running. */
    [Fact(Explicit = true)]
    public async Task Cli_AttachesToATerminalSession_AndDetachesWithTheEscapeKey()
    {
        var executable = RealHostProcesses.Executable;
        var home = RealHostProcesses.CreateHome("fa");
        try
        {
            RealHostProcesses.StartHost(executable, home);
            var shell = await StartShellAsync(home);
            using var terminal = new PtyHost();
            var session = await terminal.StartAsync(new PtyStartInfo
            {
                FileName = executable,
                Arguments = ["session", "attach", $"local/{shell}"],
                WorkingDirectory = home,
                Environment = new Dictionary<string, string?> { [RemoteShell.HomeVariable] = home },
            }, cancellationToken: TestContext.Current.CancellationToken);
            var transcript = new StringBuilder();
            var pump = PumpAsync(session, transcript);

            await WaitForTextAsync(transcript, "attaching to");
            await session.WriteAsync(Encoding.UTF8.GetBytes($"{TestShell.EchoAnswer("fleet")}\r"), Ct);
            await WaitForTextAsync(transcript, "fleet-42");

            session.Resize(rows: 31, columns: 91);
            var socketPath = PtyDaemonPaths.SocketPath(home);
            var portFile = OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile(home) : null;
            var geometry = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, portFile, Ct), item => item.Id == shell);
            while (geometry.Rows != 31 || geometry.Columns != 91)
            {
                await Task.Delay(100, Ct);
                geometry = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, portFile, Ct), item => item.Id == shell);
            }
            Assert.Equal(31, geometry.Rows);
            Assert.Equal(91, geometry.Columns);
            await session.WriteAsync(new[] { ConsoleTunnel.DetachKey }, Ct);
            // ConPTY can tear down with the CLI's last line still unrendered, so a clean exit of the CLI also counts as having detached.
            await FleetConnectionTests.WaitForAsync(() => TranscriptContains(transcript, "detached from") || session.Status != PtySessionStatus.Running);
            await pump;
            Assert.True(TranscriptContains(transcript, "detached from") || session.ExitCode == 0, Snapshot(transcript));
            await WaitForSessionStatusAsync(home, shell, HostSessionStatuses.Running);
        }
        finally
        {
            RealHostProcesses.KillAll(home);
            TempTree.Delete(home);
        }
    }

    private static async Task<string> StartShellAsync(MachineConnection connection, string cwd)
    {
        var lease = await connection.WaitConnectedAsync(Ct);
        return await lease.Client.Pty.StartPtyAsync(new RemotePtyStartRequest(TestShell.FileName, TestShell.Arguments, cwd, Title: "fleet-shell"), Ct);
    }

    /** Starts a shell on the host of a home whose host is already running. */
    private static async Task<string> StartShellAsync(string home)
    {
        await using var connection = new MachineConnection(MachineSpec.Local(home), FleetConnectionTests.TestOptions);
        connection.Start();
        await connection.WaitSettledAsync(Ct);
        return await StartShellAsync(connection, home);
    }

    private static async Task WaitForSessionStatusAsync(string home, string sessionId, string status)
    {
        await using var connection = new MachineConnection(MachineSpec.Local(home), FleetConnectionTests.TestOptions);
        connection.Start();
        await connection.WaitSettledAsync(Ct);
        try
        {
            await FleetConnectionTests.WaitForAsync(() => connection.Sessions.Any(session => session.SessionId == sessionId && session.Status == status));
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            var sessions = string.Join(", ", connection.Sessions.Select(session => $"{session.SessionId}={session.Status}"));
            Assert.Fail($"{sessionId} never became {status}; connection {connection.State} ({connection.Detail}); sessions: {sessions}");
        }
    }

    private static async Task PumpAsync(PtySession session, StringBuilder transcript)
    {
        var buffer = new byte[4096];
        while (session.Status == PtySessionStatus.Running)
        {
            int read;
            try
            {
                read = await session.ReadAsync(buffer, Ct);
            }
            catch (Exception) when (!Ct.IsCancellationRequested)
            {
                // The child exited (EIO on Unix).
                return;
            }

            if (read == 0)
            {
                await Task.Delay(5, Ct);
                continue;
            }

            lock (transcript)
                transcript.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    private static Task WaitForTextAsync(StringBuilder transcript, string text)
        => FleetConnectionTests.WaitForAsync(() => TranscriptContains(transcript, text));

    private static bool TranscriptContains(StringBuilder transcript, string text)
        => Snapshot(transcript).Contains(text, StringComparison.Ordinal);

    private static string Snapshot(StringBuilder transcript)
    {
        lock (transcript)
            return transcript.ToString();
    }
}




