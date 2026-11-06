using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Plugins.Sandbox;
using Dsh.Transport;

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
            await using var stream = new StandardIoDuplexStream(process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream, ownsStreams: false);
            await using var peer = new JsonRpcPeer(stream, PluginSandboxMethods.MaximumFrameBytes);
            var tools = new ConcurrentDictionary<string, PluginSandboxToolRequest>(StringComparer.Ordinal);
            peer.RequestHandler = (message, _) =>
            {
                if (message.Method == PluginSandboxMethods.RegisterTool)
                {
                    var tool = message.Params!.Value.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxToolRequest)!;
                    Assert.True(tools.TryAdd(tool.Package, tool));
                }
                else if (message.Method == PluginSandboxMethods.UnregisterTool)
                {
                    var tool = message.Params!.Value.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxUnregisterTool)!;
                    Assert.True(tools.TryRemove(tool.Package, out var removed));
                    Assert.NotNull(removed);
                    Assert.Equal(tool.ToolId, removed.ToolId);
                }
                else
                    throw new JsonRpcException(JsonRpcPeer.MethodNotFound, "Unexpected worker callback.");
                return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new PluginSandboxAck(),
                    PluginSandboxJsonContext.Default.PluginSandboxAck));
            };
            peer.Start();
            var ready = (await peer.RequestAsync(PluginSandboxMethods.Ready, cancellationToken: signal))!.Value
                .Deserialize(PluginSandboxJsonContext.Default.PluginSandboxReady)!;
            Assert.Equal(PluginSandboxMethods.Version, ready.Version);
            Assert.Equal(process.Id, ready.Pid);
            var unknown = await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync("sandbox/unknown", cancellationToken: signal));
            Assert.Equal(JsonRpcPeer.MethodNotFound, unknown.Code);

            var artifact = Path.Combine(root, "Dsh.SandboxPluginSample.dll");
            var inspection = (await peer.RequestAsync(PluginSandboxMethods.Inspect,
                JsonSerializer.SerializeToElement(new PluginSandboxInspectRequest(artifact),
                    PluginSandboxJsonContext.Default.PluginSandboxInspectRequest), signal))!.Value
                .Deserialize(PluginSandboxJsonContext.Default.PluginSandboxInspection)!;
            Assert.Empty(inspection.Errors);
            Assert.True(inspection.Released);
            Assert.Equal(new[] { "test/sandbox-alpha", "test/sandbox-beta" }, inspection.Packages.Select(package => package.Package).Order());
            using var configuration = JsonDocument.Parse("{}");
            using var arguments = JsonDocument.Parse("{\"operation\":\"identity\"}");
            foreach (var package in new[] { "test/sandbox-alpha", "test/sandbox-beta" })
            {
                var loaded = await peer.RequestAsync(PluginSandboxMethods.Load,
                    JsonSerializer.SerializeToElement(new PluginSandboxLoadRequest(artifact, package, configuration.RootElement),
                        PluginSandboxJsonContext.Default.PluginSandboxLoadRequest), signal);
                Assert.True(loaded!.Value.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxAck)!.Success);
                var tool = tools[package];
                var identity = (await peer.RequestAsync(PluginSandboxMethods.Invoke,
                    JsonSerializer.SerializeToElement(new PluginSandboxInvokeRequest(package, tool.ToolId, arguments.RootElement,
                        Guid.NewGuid().ToString("N")), PluginSandboxJsonContext.Default.PluginSandboxInvokeRequest), signal))!.Value;
                Assert.Equal(ready.Pid, identity.GetProperty("processId").GetInt32());
                Assert.Equal(ready.Pid, identity.GetProperty("constructorProcessId").GetInt32());
                Assert.Equal(ready.Pid, identity.GetProperty("initializationProcessId").GetInt32());
            }
            Assert.Equal(2, tools.Count);
            foreach (var package in tools.Keys.ToArray())
            {
                var unloaded = await peer.RequestAsync(PluginSandboxMethods.Unload,
                    JsonSerializer.SerializeToElement(new PluginSandboxUnloadRequest(package),
                        PluginSandboxJsonContext.Default.PluginSandboxUnloadRequest), signal);
                Assert.True(unloaded!.Value.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxAck)!.Success);
            }
            Assert.Empty(tools);
            process.StandardInput.Close();
            await process.WaitForExitAsync(signal);
            Assert.Equal(0, process.ExitCode);
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
            Assert.Equal(second is null ? 0 : 2, process.ExitCode);
            if (second is null)
                Assert.Contains("usage:", await output, StringComparison.OrdinalIgnoreCase);
            else
                Assert.Empty(await output);
            Assert.Empty(await error);
        }
        finally { await StopAsync(process, root); }
    }

    private static Process Start(string consumer, string root, params string[] args)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var executable = consumer switch
        {
            "tests" => Path.Combine(AppContext.BaseDirectory, "Dsh.Tests" + suffix),
            "cli" => RealHostProcesses.Executable,
            "web" => Path.Combine(repository, "Dsh.WebHost", "bin", "Debug", "net10.0", "dsharp-web" + suffix),
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
