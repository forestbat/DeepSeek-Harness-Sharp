using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Lsp;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class LspTests
{
    private const int HostProcessTimeoutMilliseconds = 30_000;

    [Fact(Timeout = 5000)]
    public async Task Initialize_ReturnsCapabilitiesAndServerInfo()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"processId":1,"capabilities":{}}}
            """);
        var output = new JsonRpcLineCapture();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        Assert.Equal("dsh-lsp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        await transport.StopAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task Hover_ReturnsPlainText()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"2","method":"textDocument/hover","params":{"textDocument":{"uri":"file:///a.cs"},"position":{"line":0,"character":0}}}
            """);
        var output = new JsonRpcLineCapture();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("plaintext", result.GetProperty("contents").GetProperty("kind").GetString());
        await transport.StopAsync();
    }

    [Fact]
    public async Task StandardFramedLspInitializesAndShutsDown()
    {
        using var input = CreateConversation();
        using var output = new MemoryStream();
        Assert.Equal(0, await new LspServer().RunStdioAsync(input, output, TestContext.Current.CancellationToken));
        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Content-Length:", wire, StringComparison.Ordinal);
        Assert.Contains("\"hoverProvider\":true", wire, StringComparison.Ordinal);
        Assert.Contains("\"id\":2,\"result\":null", wire, StringComparison.Ordinal);
    }

    [Theory(Timeout = HostProcessTimeoutMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingHostStdioServesOnlyTheEnabledLspCapability(bool enabled)
    {
        var directory = TempTree.CreateDirectory("host-stdio-lsp");
        try
        {
            var home = new HarnessHome(Path.Combine(directory, "home"));
            Directory.CreateDirectory(home.Root);
            var enabledValue = enabled ? "true" : "false";
            await File.WriteAllTextAsync(home.SettingsFile, $"""
                logging:
                  file: false
                plugins:
                  "@deepseek-ai/dsh-ide": false
                  "@deepseek-ai/dsh-lsp": {enabledValue}
                """, TestContext.Current.CancellationToken);
            var (exitCode, wire) = await RunHostConversationAsync(home, TestContext.Current.CancellationToken);
            Assert.Equal(enabled ? 0 : 1, exitCode);
            if (enabled)
            {
                Assert.Contains("\"hoverProvider\":true", wire, StringComparison.Ordinal);
                Assert.Contains("\"id\":2,\"result\":null", wire, StringComparison.Ordinal);
            }
            else Assert.Empty(wire);
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }

    private static MemoryStream CreateConversation()
    {
        var input = new MemoryStream();
        foreach (var json in new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""",
        })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"));
            input.Write(bytes);
        }
        input.Position = 0;
        return input;
    }

    private static Process StartHost(HarnessHome home)
    {
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        var assembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../DeepSeek-Harness-Sharp/bin", framework.Parent!.Name, framework.Name, "dsharp.dll"));
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(home.Root)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { assembly, "host", "serve", "--stdio" }) start.ArgumentList.Add(argument);
        start.Environment[HarnessHome.ChildHomeEnv] = home.Root;
        return TestProcessScope.Start(start)!;
    }

    private static async Task<(int ExitCode, string Wire)> RunHostConversationAsync(HarnessHome home, CancellationToken cancellationToken)
    {
        using var process = StartHost(home);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var input = CreateConversation();
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await error;
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

}
