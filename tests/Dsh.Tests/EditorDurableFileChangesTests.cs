using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Ide.Surfaces;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Ptc;
using Dsh.Runtime;
using Dsh.Tools;

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
        var arguments = new JsonObject();
        var tool = StrReplaceEditorTool.ToolName;
        if (operation is "write" or "edit")
        {
            tool = operation;
            arguments["file_path"] = path;
            if (operation == "write")
                arguments["content"] = "after";
            else
            {
                File.WriteAllText(path, "before");
                arguments["old_string"] = "before";
                arguments["new_string"] = "after";
            }
        }
        else
        {
            arguments["command"] = operation;
            arguments["path"] = path;
            if (operation == "create")
                arguments["file_text"] = "after";
            else
            {
                File.WriteAllText(path, "before");
                arguments["new_str"] = "after";
                if (operation == "insert")
                    arguments["insert_line"] = 0;
                else
                    arguments["old_str"] = "before";
            }
        }
        fixture.Session.Append(new TurnStartPayload(1));
        Assert.False((await fixture.Execute(tool, arguments)).IsError);
        fixture.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        var change = Assert.Single(fixture.Session.SnapshotEvents(), item => item.Data is FileChangedPayload);
        Assert.Equal(path, Assert.IsType<FileChangedPayload>(change.Data).Path);
        Assert.True(change.Ignorable);
        Assert.False(change.IsSurfaceEligible);
        Assert.Empty(fixture.Session.DeriveMessages());
        Assert.Contains("after", File.ReadAllText(path));
        using var serialized = JsonDocument.Parse(DshJson.Serialize(change));
        Assert.Equal(["path", "type"], serialized.RootElement.GetProperty("data").EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal([path], EditorChangeSet.PreviousTurnFiles(fixture.Session));
        Assert.Equal([path], EditorChangeSet.PreviousTurnFiles(await fixture.Restore()));
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
