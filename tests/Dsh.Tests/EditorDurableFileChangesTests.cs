using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Ide.Surfaces;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Ptc;
using Dsh.Runtime;
using Dsh.Tools;
using Dsh.Subprocess;
using Dsh.Agent;
using Dsh.Safety;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class EditorDurableFileChangesTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("create")]
    [InlineData("str_replace")]
    [InlineData("insert")]
    public async Task MutationTools_RecordOnlyCommittedFiles(string operation)
    {
        using var fixture = new MutationFixture();
        var path = Path.Combine(fixture.Root, "changed.txt");
        const string before = "before-long-文档";
        const string after = "after-✓";
        if (operation != "create")
        {
            File.WriteAllText(path, before);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        await using var previous = operation == "create" ? null
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var (tool, arguments) = Replacement(operation, path, before, after);
        fixture.Session.Append(new TurnStartPayload(1));

        Assert.False((await fixture.Execute(tool, arguments)).IsError);
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        var expected = operation == "insert" ? "after-✓\nbefore-long-文档" : after;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
        if (previous is not null)
        {
            using var reader = new StreamReader(previous, leaveOpen: true);
            Assert.Equal(before, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        var change = Assert.Single(fixture.Session.SnapshotEvents(), item => item.Data is FileChangedPayload);
        Assert.Equal(path, Assert.IsType<FileChangedPayload>(change.Data).Path);
        Assert.Empty(fixture.Session.DeriveMessages());
        Assert.Equal([path], EditorChangeSet.PreviousTurnFiles(fixture.Session));
        Assert.Equal([path], EditorChangeSet.PreviousTurnFiles(await fixture.Restore()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite
                | (operation is "str_replace" or "insert" ? UnixFileMode.GroupRead : 0), File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task ReadOnlyAndFailedMutations_DoNotRecordChanges()
    {
        using var fixture = new MutationFixture();
        var path = Path.Combine(fixture.Root, "read-only.txt");
        File.WriteAllText(path, "unchanged");
        fixture.Session.Append(new TurnStartPayload(1));
        Assert.False((await fixture.Execute("str_replace_editor", new JsonObject { ["command"] = "view", ["path"] = path })).IsError);
        Assert.True((await fixture.Execute("str_replace_editor", new JsonObject { ["command"] = "create", ["path"] = path, ["file_text"] = "rejected" })).IsError);
        Assert.True((await fixture.Execute("str_replace_editor", new JsonObject { ["command"] = "str_replace", ["path"] = path, ["old_str"] = "missing", ["new_str"] = "rejected" })).IsError);
        Assert.True((await fixture.Execute("str_replace_editor", new JsonObject { ["command"] = "insert", ["path"] = path, ["insert_line"] = 99, ["new_str"] = "rejected" })).IsError);
        Assert.True((await fixture.Execute("edit", new JsonObject { ["file_path"] = path, ["old_string"] = "missing", ["new_string"] = "rejected" })).IsError);
        Assert.True((await fixture.Execute("write", new JsonObject { ["file_path"] = fixture.Root, ["content"] = "rejected" })).IsError);
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        Assert.Equal("unchanged", File.ReadAllText(path));
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is FileChangedPayload);
        Assert.Empty(EditorChangeSet.PreviousTurnFiles(fixture.Session));
        Assert.Empty(EditorChangeSet.PreviousTurnFiles(await fixture.Restore()));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("str_replace")]
    [InlineData("insert")]
    public async Task MutationTools_FailedPublicationPreservesOriginalAndDoesNotRecordChanges(string operation)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows delete-sharing denial is required for this publication failure.");
        using var fixture = new MutationFixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "files")).FullName;
        var path = Path.Combine(directory, "unchanged.txt");
        const string before = "before-long-文档";
        File.WriteAllText(path, before);
        using var previous = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fixture.Session.Append(new TurnStartPayload(1));
        var (tool, arguments) = Replacement(operation, path, before, "after-✓");

        var result = await fixture.Execute(tool, arguments);
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        Assert.True(result.IsError);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(directory));
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is FileChangedPayload);
        Assert.Empty(EditorChangeSet.PreviousTurnFiles(await fixture.Restore()));
    }

    [Theory]
    [InlineData(FileMode.Open, false)]
    [InlineData(FileMode.CreateNew, true)]
    public async Task OrdinaryMutationModes_RejectMissingOpenAndExistingCreateNew(FileMode mode, bool exists)
    {
        using var fixture = new MutationFixture();
        var path = Path.Combine(fixture.Root, "mode.txt");
        if (exists)
            File.WriteAllText(path, "before");
        var call = ToolCallId.Create("ordinary-mode-test");
        var run = new ToolRunContext
        {
            CallId = call, RootCallIdValue = call, Name = "write",
            Arguments = JsonSerializer.SerializeToElement(new { }),
            Agent = fixture.Agent, Signal = TestContext.Current.CancellationToken,
        };

        await Assert.ThrowsAnyAsync<IOException>(() => SettingsMutation.WriteTextAsync(path, "before", "after", run, mode));

        Assert.Equal(exists, File.Exists(path));
        if (exists)
            Assert.Equal("before", File.ReadAllText(path));
        Assert.DoesNotContain(fixture.Session.SnapshotEvents(), item => item.Data is FileChangedPayload);
    }

    private static (string Tool, JsonObject Arguments) Replacement(string operation, string path, string before, string after)
        => operation switch
        {
            "write" => ("write", new JsonObject { ["file_path"] = path, ["content"] = after }),
            "edit" => ("edit", new JsonObject { ["file_path"] = path, ["old_string"] = before, ["new_string"] = after }),
            "create" => (StrReplaceEditorTool.ToolName, new JsonObject { ["command"] = operation, ["path"] = path, ["file_text"] = after }),
            "str_replace" => (StrReplaceEditorTool.ToolName, new JsonObject { ["command"] = operation, ["path"] = path, ["old_str"] = before, ["new_str"] = after }),
            "insert" => (StrReplaceEditorTool.ToolName, new JsonObject { ["command"] = operation, ["path"] = path, ["insert_line"] = 0, ["new_str"] = after }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrappedMutations_PersistReplayAndRespectCompletedTurnBoundaries(bool throughPtc)
    {
        using var fixture = new MutationFixture();
        fixture.Session.Append(new TurnStartPayload(1));
        Assert.False((await fixture.Execute("write", new JsonObject { ["file_path"] = "older.txt", ["content"] = "older" })).IsError);
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        var start = fixture.Session.Append(new TurnStartPayload(2)).Seq;
        var inner = new JsonObject { ["name"] = "write", ["arguments"] = new JsonObject { ["file_path"] = "wrapped.txt", ["content"] = "wrapped" } };
        ToolExecutionResult result;
        if (throughPtc)
        {
            var literal = JsonSerializer.Serialize(inner.ToJsonString());
            result = await fixture.Execute(ToolRunCode.ToolName, new JsonObject
            {
                ["code"] = $"return await tools.call(\"toon\", JsonNode.Parse({literal})!.AsObject());",
                ["description"] = "Persist a nested file mutation",
            });
        }
        else
            result = await fixture.Execute(Dsh.Toon.Plugin.ToolName, inner);
        Assert.False(result.IsError, string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Null(result.Meta);
        var nextDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "next-directory")).FullName;
        fixture.Session.Append(new WorkingDirectoryChangedPayload(nextDirectory));
        fixture.Session.Append(new TurnEndPayload(2, new TurnEndReason.Completed()));
        fixture.Session.Append(new TurnStartPayload(3));
        Assert.False((await fixture.Execute("write", new JsonObject { ["file_path"] = "ongoing.txt", ["content"] = "ongoing" })).IsError);

        var expected = Path.Combine(fixture.Root, "wrapped.txt");
        Assert.Equal("wrapped", File.ReadAllText(expected));
        Assert.Equal([expected], EditorChangeSet.PreviousTurnFiles(fixture.Session));
        Assert.Equal(start, EditorChangeSet.PreviousTurnStart(fixture.Session));
        var restored = await fixture.Restore();
        Assert.Equal([expected], EditorChangeSet.PreviousTurnFiles(restored));
        Assert.Equal(start, EditorChangeSet.PreviousTurnStart(restored));
        Assert.Equal(3, restored.SnapshotEvents().Count(item => item.Data is FileChangedPayload));
    }

    [Fact]
    public async Task LegacyDiffMetadata_RemainsReadableAndDeduplicated()
    {
        using var fixture = new MutationFixture();
        fixture.Session.Append(new TurnStartPayload(1));
        var result = await fixture.Execute("write", new JsonObject { ["file_path"] = "legacy.txt", ["content"] = "legacy" });
        Assert.False(result.IsError);
        var message = MessageFactory.CreateToolResultMessage(ToolCallId.Create("legacy"), result.Content, false);
        fixture.Session.Append(new ToolResultPayload(1, 1, message, Meta: result.Meta), new SurfaceOp.Append());
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        var expected = Path.Combine(fixture.Root, "legacy.txt");
        Assert.Equal([expected], EditorChangeSet.PreviousTurnFiles(fixture.Session));

        using var legacyFixture = new MutationFixture();
        legacyFixture.Session.Append(new TurnStartPayload(1));
        legacyFixture.Session.Append(new WorkingDirectoryChangedPayload(fixture.Root));
        legacyFixture.Session.Append(new ToolResultPayload(1, 1, message, Meta: result.Meta), new SurfaceOp.Append());
        legacyFixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        Assert.Equal([expected], EditorChangeSet.PreviousTurnFiles(await legacyFixture.Restore()));
    }

    private sealed class MutationFixture : IDisposable
    {
        private readonly Context _ctx = new();
        private readonly SessionStore _store;
        private readonly SubprocessService _subprocess;
        private readonly List<IDisposable> _registrations;
        private readonly ISessionPersistence _persistence;

        public MutationFixture()
        {
            var project = new DirectoryInfo(AppContext.BaseDirectory);
            while (project is not null && !File.Exists(Path.Combine(project.FullName, "DeepSeek-Harness-Sharp.slnx")))
                project = project.Parent;
            Root = Path.Combine((project ?? throw new InvalidOperationException("Repository root not found")).FullName, "plans", $"editor-mutations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            _ctx.SetOwn("dshHomePath", Path.Combine(Root, "home"));
            _ = new SystemPrompt(_ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(_ctx);
            _ = new LlmRuntime(_ctx);
            _ = new CommandsService(_ctx);
            _store = new SessionStore(_ctx);
            _subprocess = new SubprocessService(_ctx);
            _registrations =
            [
                ToolRunCode.Register(_ctx, Root, debugBatching: false),
                new Dsh.Persistence.Plugin("@deepseek-ai/dsh-persistence").Apply(_ctx, null),
                new Dsh.Toon.Plugin().Apply(_ctx, null),
                WriteTool.Register(_ctx),
                EditTool.Register(_ctx),
                StrReplaceEditorTool.Register(_ctx),
            ];
            _persistence = _ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            Session = _store.Create(id, header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Root,
                IsSeeded = false,
            });
            Agent = new AgentLoopAgent(_ctx, id, new AgentOptions(), Session, _ => 0);
        }

        public string Root { get; }
        public Session Session { get; }
        public AgentLoopAgent Agent { get; }
        public ToolRuntime Tools { get; }

        public Task<ToolExecutionResult> Execute(string name, JsonObject arguments)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")),
                Name = name,
                Arguments = JsonSerializer.SerializeToElement(arguments),
                Agent = Agent,
                Signal = TestContext.Current.CancellationToken,
            });

        public async Task<Session> Restore()
        {
            await _store.Flush(Session);
            using var handle = _persistence.Open(Session.Id, SessionAccess.Read);
            return Session.FromRestore(Session.Id, handle.Read(), handle.Header, handle.InheritedEventCount);
        }

        public void Dispose()
        {
            Agent.Dispose();
            _store.Release(Session.Id);
            foreach (var registration in _registrations.AsEnumerable().Reverse())
                registration.Dispose();
            _subprocess.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
