using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Plugins.Native;
using Dsh.Plugins.Sandbox;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed unsafe class NativeHostCallBufferTests
{
    [Fact]
    public void InvalidBuffers_DoNotExecuteAppend_AndFullFrameExecutesOnce()
    {
        var root = TempTree.CreateDirectory("native-host-exchange");
        try
        {
            var host = CreateHost(root);
            var path = Path.Combine(root, "append.txt");
            // Synthetic test input exercises a destructive operation through the real broker.
            var arguments = JsonSerializer.Serialize(new { path, data = "b25jZQ==", append = true });
            string Call(string operation, string json) => CallHost(host, operation, json);
            Assert.Equal(DshNativePluginAbi.Error,
                NativeHostCallBuffer.Invoke(PluginHostOperations.WriteFile, arguments, null, 0, Call));
            Assert.Equal(DshNativePluginAbi.Error,
                NativeHostCallBuffer.Invoke(PluginHostOperations.WriteFile, arguments, null, DshNativePluginAbi.MaxResultBytes, Call));
            byte* shortBuffer = stackalloc byte[1];
            shortBuffer[0] = 0x5a;
            Assert.Equal(DshNativePluginAbi.Error,
                NativeHostCallBuffer.Invoke(PluginHostOperations.WriteFile, arguments, shortBuffer, 1, Call));
            Assert.Equal(0x5a, shortBuffer[0]);
            Assert.False(File.Exists(path));

            var bytes = new byte[DshNativePluginAbi.MaxResultBytes + 2];
            bytes[0] = bytes[^1] = 0x5a;
            int written;
            fixed (byte* output = bytes)
                written = NativeHostCallBuffer.Invoke(PluginHostOperations.WriteFile, arguments, output + 1, bytes.Length - 2, Call);
            Assert.InRange(written, 1, DshNativePluginAbi.MaxResultBytes);
            Assert.Equal(0x5a, bytes[0]);
            Assert.Equal(0x5a, bytes[^1]);
            Assert.Equal("once", File.ReadAllText(path));
            using var result = JsonDocument.Parse(bytes.AsMemory(1, written));
            Assert.Equal(4, result.RootElement.GetProperty("result").GetProperty("bytesWritten").GetInt32());
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void FailedOperation_ReturnsError_AndNextExchangeIsIndependent()
    {
        var root = TempTree.CreateDirectory("native-host-error");
        try
        {
            var host = CreateHost(root);
            var path = Path.Combine(root, "missing.txt");
            var arguments = JsonSerializer.Serialize(new { path });
            string Call(string operation, string json) => CallHost(host, operation, json);
            var bytes = new byte[DshNativePluginAbi.MaxResultBytes];
            int written;
            fixed (byte* output = bytes)
                written = NativeHostCallBuffer.Invoke(PluginHostOperations.ReadFile, arguments, output, bytes.Length, Call);
            Assert.InRange(written, 1, bytes.Length);
            using (var result = JsonDocument.Parse(bytes.AsMemory(0, written)))
            {
                Assert.False(string.IsNullOrWhiteSpace(result.RootElement.GetProperty("error").GetString()));
                Assert.False(result.RootElement.TryGetProperty("result", out _));
            }
            File.WriteAllText(path, "synthetic second exchange");
            fixed (byte* output = bytes)
                written = NativeHostCallBuffer.Invoke(PluginHostOperations.ReadFile, arguments, output, bytes.Length, Call);
            Assert.InRange(written, 1, bytes.Length);
            using var next = JsonDocument.Parse(bytes.AsMemory(0, written));
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic second exchange")),
                next.RootElement.GetProperty("result").GetProperty("data").GetString());
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
        var settings = new HarnessSettings { Safety = new SafetySettings { AutoApprove = true } };
        _ = new SafetyPolicySource(context, () => settings);
        return new PluginCapabilityFactory(context, new PluginExecutionEnvironment(new HarnessHome(root), settings, root))
            .Create("test.native-host-buffer", context);
    }

    private static string CallHost(IPluginHost host, string operation, string json)
    {
        using var arguments = JsonDocument.Parse(json);
        return host.CallAsync(operation, arguments.RootElement, TestContext.Current.CancellationToken)
            .AsTask().GetAwaiter().GetResult().GetRawText();
    }
}
