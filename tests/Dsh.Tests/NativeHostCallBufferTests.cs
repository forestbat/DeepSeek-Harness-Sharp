using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Plugins.Native;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed unsafe class NativeHostCallBufferTests
{
    [Fact]
    public void SizeProbeAndShortBuffer_AppendOnlyOnceThroughRealBroker()
    {
        var root = TempTree.CreateDirectory("native-host-retry");
        try
        {
            var host = CreateHost(root);
            var path = Path.Combine(root, "append.txt");
            var arguments = JsonSerializer.Serialize(new
            {
                path,
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes("once")),
                append = true,
            });
            using var replies = new NativeHostCallBuffer();
            string Call(string operation, string json) => CallHost(host, operation, json);
            var required = replies.Invoke(PluginHostOperations.WriteFile, arguments, null, 0, Call);
            Assert.True(required > 1);
            byte* shortBuffer = stackalloc byte[1];
            Assert.Equal(required, replies.Invoke(PluginHostOperations.WriteFile, arguments, shortBuffer, 1, Call));
            var bytes = new byte[required];
            fixed (byte* output = bytes)
                Assert.Equal(required, replies.Invoke(PluginHostOperations.WriteFile, arguments, output, bytes.Length, Call));
            Assert.Equal("once", File.ReadAllText(path));
            using var result = JsonDocument.Parse(bytes);
            Assert.Equal(4, result.RootElement.GetProperty("result").GetProperty("bytesWritten").GetInt32());
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void FailedOperation_IsRetainedAndReturnedAsAnError()
    {
        var root = TempTree.CreateDirectory("native-host-error");
        try
        {
            var host = CreateHost(root);
            var path = Path.Combine(root, "missing.txt");
            var arguments = JsonSerializer.Serialize(new { path });
            using var replies = new NativeHostCallBuffer();
            string Call(string operation, string json) => CallHost(host, operation, json);
            var required = replies.Invoke(PluginHostOperations.ReadFile, arguments, null, 0, Call);
            Assert.True(required > 0);
            // If the retry repeats the operation, it would now succeed instead of returning the original failure.
            File.WriteAllText(path, "created after failed probe");
            var bytes = new byte[required];
            fixed (byte* output = bytes)
                Assert.Equal(required, replies.Invoke(PluginHostOperations.ReadFile, arguments, output, bytes.Length, Call));
            using var result = JsonDocument.Parse(bytes);
            Assert.False(string.IsNullOrWhiteSpace(result.RootElement.GetProperty("error").GetString()));
            Assert.False(result.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void PendingReply_CannotBeReplacedAndDeactivationRefusesMoreCalls()
    {
        var root = TempTree.CreateDirectory("native-host-pending");
        try
        {
            var host = CreateHost(root);
            var path = Path.Combine(root, "append.txt");
            var arguments = JsonSerializer.Serialize(new { path, data = "eA==", append = true });
            using var replies = new NativeHostCallBuffer();
            string Call(string operation, string json) => CallHost(host, operation, json);
            Assert.True(replies.Invoke(PluginHostOperations.WriteFile, arguments, null, 0, Call) > 0);
            Assert.Equal(DshNativePluginAbi.Error,
                replies.Invoke(PluginHostOperations.WriteFile, arguments + " ", null, 0, Call));
            replies.Dispose();
            Assert.Equal(DshNativePluginAbi.Error,
                replies.Invoke(PluginHostOperations.WriteFile, arguments, null, 0, Call));
            Assert.Equal("x", File.ReadAllText(path));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void PendingReplyBudget_IsSharedAcrossThreads()
    {
        var root = TempTree.CreateDirectory("native-host-budget");
        try
        {
            var path = Path.Combine(root, "large-response.json");
            File.WriteAllText(path, "{\"payload\":\"" + new string('x', 1024 * 1024) + "\"}");
            const string arguments = "{}";
            using var replies = new NativeHostCallBuffer();
            var retained = 0;
            var refused = 0;
            for (var index = 0; index < 6; index++)
            {
                Exception? failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        var required = replies.Invoke("test.large-response", arguments, null, 0,
                            (_, _) => File.ReadAllText(path));
                        if (required > 0)
                            retained++;
                        else
                            refused++;
                    }
                    catch (InvalidOperationException)
                    {
                        refused++;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                });
                thread.Start();
                thread.Join();
                Assert.Null(failure);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.InRange(retained, 1, 3);
            Assert.True(refused > 0);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static IPluginHost CreateHost(string root)
    {
        var context = new Context();
        _ = new SystemPrompt(context, new SystemPromptConfig());
        _ = new ToolRuntime(context);
        return new PluginCapabilityFactory(context, new HarnessHome(root), new HarnessSettings())
            .Create("test.native-host-buffer", context);
    }

    private static string CallHost(IPluginHost host, string operation, string json)
    {
        using var arguments = JsonDocument.Parse(json);
        return host.CallAsync(operation, arguments.RootElement, TestContext.Current.CancellationToken)
            .AsTask().GetAwaiter().GetResult().GetRawText();
    }
}
