using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Ma2a.Services;
using Dsh.Ma2a.Tools;
using Dsh.Runtime;

namespace Dsh.Ma2a.ToolTests;

public sealed class Ma2aToolSafetyTests : IDisposable
{
    private readonly Context _context = new();
    private readonly ToolRuntime _runtime;
    private readonly Ma2aTools _tools = new();
    private readonly SafetySettings _policy = new() { AutoApprove = true };
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "plans", $"ma2a-tool-{Guid.NewGuid():N}");

    public Ma2aToolSafetyTests()
    {
        _ = new SystemPrompt(_context, new SystemPromptConfig());
        _runtime = new ToolRuntime(_context);
        _ = new LlmRuntime(_context);
        _ = new SafetyPolicySource(_context, () => new HarnessSettings { Safety = _policy });
        _tools.Register(_context);
    }

    [Theory]
    [InlineData("upload", "read", "")]
    [InlineData("download", "write", "")]
    [InlineData("download", "write", ".dsh-ma2a-part")]
    [InlineData("download", "write", ".dsh-ma2a-part.meta")]
    [InlineData("download", "write", ".dsh-ma2a-part.meta.new")]
    [InlineData("download", "write", ".dsh-ma2a-part.lock")]
    [InlineData("download", "read", ".dsh-ma2a-part")]
    [InlineData("download", "read", ".dsh-ma2a-part.meta")]
    public async Task TransferHonorsActualLocalFilePolicyBeforeOpeningFilesOrConnecting(string direction, string intent, string suffix)
    {
        var target = Path.Combine(_directory, "payload.bin");
        _policy.Blacklist["blocked-transfer-file"] = new PermissionMatcher { Path = $"{target}{suffix}", Intent = intent };

        var result = await ExecuteAsync("ma2a_transfer", TransferArguments(direction, target));

        Assert.True(result.IsError);
        Assert.Contains("blocked-transfer-file", Text(result));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task PrepareReturnsAUsableNativeRequestWithBinaryIdentifiers()
    {
        var result = await ExecuteAsync("ma2a_service_prepare", new JsonObject
        {
            ["operation"] = "agent.cancel",
            ["arguments"] = new JsonObject { ["agent"] = "research", ["task"] = "00000000000000000000000000000001" },
        });

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        var prepared = JsonNode.Parse(success.Value.GetRawText())!.AsObject();
        var encoded = Ma2aServiceSession.EncodeOpen(new string('1', 64), prepared["service"]!.GetValue<string>(), new string('2', 32), prepared["body"]!);
        Assert.NotEmpty(encoded);
        var open = Ma2aCbor.Decode(encoded)!.AsObject();
        Assert.Equal(8UL, open["3"]!.GetValue<ulong>());
        Assert.NotNull(open["4"]);
    }

    [Fact]
    public async Task LocalCommandDescriptionPreservesReplayPolicyAndTypedFields()
    {
        var result = await ExecuteAsync("ma2a_describe", new JsonObject { ["operation"] = "space_grant" });

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        Assert.Equal("space_grant", success.Value.GetProperty("operation").GetString());
        var fields = success.Value.GetProperty("command").GetProperty("fields");
        Assert.True(fields.TryGetProperty("request_id", out _));
        Assert.True(fields.TryGetProperty("files_read", out _));
        Assert.Equal("conflict", success.Value.GetProperty("request_id_policy").GetProperty("different_payload").GetString());
    }

    [Fact]
    public void DisposingToolsUnregistersItsPublicSurface()
    {
        Assert.NotNull(_runtime.Get("ma2a_transfer"));
        _tools.Dispose();
        Assert.Null(_runtime.Get("ma2a_transfer"));
        Assert.Null(_runtime.Get("ma2a_stream_open"));
    }

    [Fact]
    public async Task SpaceInvitationHonorsItsActualOutputPathPolicyBeforeCallingRuntime()
    {
        var invitation = Path.Combine(_directory, "invitation");
        _policy.Blacklist["blocked-invitation-output"] = new PermissionMatcher { Path = invitation, Intent = "write" };
        var result = await ExecuteAsync("ma2a_command", new JsonObject
        {
            ["state_directory"] = Path.Combine(_directory, "absent-runtime"),
            ["command"] = new JsonObject
            {
                ["operation"] = "space_invite", ["request_id"] = new string('2', 32),
                ["space_id"] = new string('1', 64), ["ttl_ms"] = 300_000, ["output_path"] = invitation,
            },
        });
        Assert.True(result.IsError);
        Assert.Contains("blocked-invitation-output", Text(result));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task DownloadCannotReplaceTheActiveSettingsDocument()
    {
        var home = HarnessHome.Resolve(Path.Combine(_directory, "home"));
        _context.SetOwn("harnessOptions", new HarnessOptions(home));
        var result = await ExecuteAsync("ma2a_transfer", TransferArguments("download", home.SettingsFile));
        Assert.True(result.IsError);
        Assert.Contains("settings", Text(result), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task NativeServiceHandlesRequireAnActualDshSession()
    {
        var result = await ExecuteAsync("ma2a_stream_open", new JsonObject
        {
            ["state_directory"] = Path.Combine(_directory, "absent-runtime"), ["target"] = new string('1', 64),
            ["service"] = "files", ["request_id"] = new string('2', 32), ["body"] = Ma2aServiceRequests.Stat("data:/"),
        });
        Assert.True(result.IsError);
        Assert.Contains("DSH session", Text(result));
        Assert.False(Directory.Exists(_directory));
    }

    private JsonObject TransferArguments(string direction, string target)
        => new()
        {
            ["state_directory"] = Path.Combine(_directory, "absent-runtime"),
            ["target"] = new string('1', 64), ["direction"] = direction,
            ["remote_path"] = "files:/payload.bin", ["local_path"] = target,
            ["request_id"] = new string('2', 32), ["resume"] = true,
        };

    private Task<ToolExecutionResult> ExecuteAsync(string name, JsonObject arguments)
        => _runtime.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"ma2a-{Guid.NewGuid():N}"), Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments), Signal = TestContext.Current.CancellationToken,
        });

    private static string Text(ToolExecutionResult result) => string.Join("\n", result.Content.OfType<TextBlock>().Select(block => block.Text));
    public void Dispose() => _tools.Dispose();
}
