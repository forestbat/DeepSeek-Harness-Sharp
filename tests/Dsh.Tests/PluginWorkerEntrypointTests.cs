using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Plugins.Sandbox;
using Dsh.Transport;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class PluginWorkerEntrypointTests
{
    [Theory(Timeout = 15000)]
    [InlineData("tests")]
    [InlineData("cli")]
    [InlineData("web")]
    public async Task OptionalModule_DispatchesRealSharedWorkerBeforeApplicationComposition(string consumer)
    {
        var signal = TestContext.Current.CancellationToken;
        var root = TempTree.CreateDirectory("worker-entry");
        Process? process = null;
        try
        {
            foreach (var suffix in new[] { ".dll", ".deps.json" })
            {
                var name = "Dsh.SandboxPluginSample" + suffix;
                File.Copy(Path.Combine(AppContext.BaseDirectory, "sandbox-plugin", name), Path.Combine(root, name));
            }
            process = Start(consumer, root, "--plugin-worker");
            var error = process.StandardError.ReadToEndAsync(signal);
            await using var stream = new StandardIoDuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            var tools = new ConcurrentDictionary<string, PluginSandboxToolRequest>(StringComparer.Ordinal);
            using var peer = JsonRpcChannel.ServeMethods(stream, PluginSandboxWire.Formatter(), new Broker(tools).Methods());
            Task<T> Ask<T>(string method, object request) => peer.InvokeAsync<T>(method, signal, request);
            PluginSandboxReady ready;
            try { ready = await Ask<PluginSandboxReady>(PluginSandboxMethods.Ready, new PluginSandboxAck()).WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (Exception handshake)
            {
                process.StandardInput.Close();
                process.Kill(entireProcessTree: true);
                string detail;
                try { detail = await error.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { detail = ""; }
                throw new InvalidOperationException($"{handshake.GetType().Name}: {handshake.Message}; stderr: {detail}", handshake);
            }            Assert.Equal(PluginSandboxMethods.Version, ready.Version);
            Assert.Equal(process.Id, ready.Pid);
            var unknown = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => peer.InvokeAsync("sandbox/unknown", null, signal));
            Assert.Equal(JsonRpcErrorCode.MethodNotFound, unknown.ErrorCode);

            var artifact = Path.Combine(root, "Dsh.SandboxPluginSample.dll");
            var inspection = await Ask<PluginSandboxInspection>(PluginSandboxMethods.Inspect, new PluginSandboxInspectRequest(artifact));
            Assert.Empty(inspection.Errors);
            Assert.True(inspection.Released);
            Assert.Equal(new[] { "test/sandbox-alpha", "test/sandbox-beta" }, inspection.Packages.Select(package => package.Package).Order());
            using var configuration = JsonDocument.Parse("{}");
            using var arguments = JsonDocument.Parse("{\"operation\":\"identity\"}");
            foreach (var package in new[] { "test/sandbox-alpha", "test/sandbox-beta" })
            {
                var loaded = await Ask<PluginSandboxAck>(PluginSandboxMethods.Load, new PluginSandboxLoadRequest(artifact, package, configuration.RootElement));                Assert.True(loaded.Success);
                var tool = tools[package];
                var identity = await Ask<JsonElement>(PluginSandboxMethods.Invoke,
                    new PluginSandboxInvokeRequest(package, tool.ToolId, arguments.RootElement, Guid.NewGuid().ToString("N")));
                Assert.Equal(ready.Pid, identity.GetProperty("processId").GetInt32());
                Assert.Equal(ready.Pid, identity.GetProperty("constructorProcessId").GetInt32());
                Assert.Equal(ready.Pid, identity.GetProperty("initializationProcessId").GetInt32());
            }
            Assert.Equal(2, tools.Count);
            foreach (var package in tools.Keys.ToArray())
            {
                var unloaded = await Ask<PluginSandboxAck>(PluginSandboxMethods.Unload, new PluginSandboxUnloadRequest(package));                Assert.True(unloaded.Success);
            }
            Assert.Empty(tools);            process.StandardInput.Close();
            await process.WaitForExitAsync(signal);            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
        }
        finally { await StopAsync(process, root); }
    }

    [Theory(Timeout = 15000)]
    [InlineData("tests", "--plugin-worker", "--unknown")]
    [InlineData("tests", "--unknown", "--plugin-worker")]
    [InlineData("cli", "--plugin-worker", "--unknown")]
    [InlineData("cli", "--unknown", "--plugin-worker")]
    [InlineData("web", "--plugin-worker", "--unknown")]
    [InlineData("web", "--unknown", "--plugin-worker")]
    [InlineData("cli", "--help", null)]
    [InlineData("web", "--help", null)]
    public async Task Arguments_ContinueNormalStartupOrRejectMalformedWorkerInvocation(string consumer, string first, string? second)
    {
        var root = TempTree.CreateDirectory("worker-args");
        Process? process = null;
        try
        {
            process = Start(consumer, root, second is null ? [first] : [first, second]);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            Assert.Equal(second is null, process.ExitCode == 0);
            Assert.Empty(second is null ? stderr : stdout);
        }
        finally { await StopAsync(process, root); }
    }

    /** 测试替身: 只记工具登记, 其余宿主能力调用一律拒绝。 */
    private sealed class Broker(ConcurrentDictionary<string, PluginSandboxToolRequest> tools)
    {
        public (string Method, Delegate Handler)[] Methods() =>
        [
            (PluginSandboxMethods.RegisterTool, (Func<PluginSandboxToolRequest, CancellationToken, Task<PluginSandboxAck>>)RegisterToolAsync),
            (PluginSandboxMethods.UnregisterTool, (Func<PluginSandboxUnregisterTool, CancellationToken, Task<PluginSandboxAck>>)UnregisterToolAsync),
            (PluginSandboxMethods.HostCall, (Func<PluginSandboxHostCall, CancellationToken, Task<JsonElement>>)HostCallAsync),
            (PluginSandboxMethods.Log, (Func<PluginSandboxLog, CancellationToken, Task<PluginSandboxAck>>)LogAsync),
        ];

        public Task<PluginSandboxAck> RegisterToolAsync(PluginSandboxToolRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(tools.TryAdd(request.Package, request));
            return Task.FromResult(new PluginSandboxAck());
        }

        public Task<PluginSandboxAck> UnregisterToolAsync(PluginSandboxUnregisterTool request, CancellationToken cancellationToken = default)
        {
            Assert.True(tools.TryRemove(request.Package, out var removed));
            Assert.NotNull(removed);
            Assert.Equal(request.ToolId, removed.ToolId);
            return Task.FromResult(new PluginSandboxAck());
        }

        public Task<JsonElement> HostCallAsync(PluginSandboxHostCall request, CancellationToken cancellationToken = default)
            => throw new LocalRpcException("Unexpected worker host call.") { ErrorCode = JsonRpcException.MethodNotFound };

        public Task<PluginSandboxAck> LogAsync(PluginSandboxLog request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PluginSandboxAck());
    }

    private static Process Start(string consumer, string root, params string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var executable = consumer switch
        {
            "tests" => Path.Combine(AppContext.BaseDirectory, "Dsh.Tests" + suffix),
            "cli" => RealHostProcesses.Executable,
            "web" => Path.Combine(repository, "Dsh.WebHost", "bin", "Debug", BuildTargetFramework.Value, "dsharp-web" + suffix),
            _ => throw new ArgumentOutOfRangeException(nameof(consumer)),
        };
        Assert.True(File.Exists(executable), "The real consumer executable must be built with the tests.");
        var info = new ProcessStartInfo(executable, args)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment[HarnessHome.HomeEnv] = root;
        return TestProcessScope.Start(info);
    }

    private static async Task StopAsync(Process? process, string root)
    {
        try
        {
            if (process is not null)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
