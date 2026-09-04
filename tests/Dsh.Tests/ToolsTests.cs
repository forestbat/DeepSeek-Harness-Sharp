using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Tests;

public class ToolsTests : IDisposable
{
    private readonly Context _ctx;
    private readonly ToolRuntime _tools;
    private readonly string _tempDir;

    public ToolsTests()
    {
        _ctx = new Context();
        _ = new SystemPrompt(_ctx, new SystemPromptConfig());
        _tools = new ToolRuntime(_ctx);
        _ = new SubprocessService(_ctx);
        _tempDir = Path.Combine(Path.GetTempPath(), $"dsh-tools-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private Task<ToolExecutionResult> Execute(string name, string arguments, IAgent? agent = null)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Agent = agent,
            Signal = default,
        });

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx, string directory)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = directory,
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }

    public sealed class Bash : IDisposable
    {
        private readonly ToolsTests _outer = new();

        public void Dispose() => _outer.Dispose();

        public Bash()
        {
            BashTool.Register(_outer._ctx);
        }

        private Task<ToolExecutionResult> Bash2(string command, long? timeoutMs = null)
            => _outer.Execute("bash", JsonSerializer.Serialize(new
            {
                command,
                description = "Run test command",
                timeoutMs,
            }, DshJson.Options));

        [Fact]
        public async Task Echo_ReturnsStdoutAndZeroExit()
        {
            var result = await Bash2("echo hello");
            Assert.False(result.IsError);
            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.Equal(0, success.Value.GetProperty("exitCode").GetInt32());
            Assert.Equal("hello\n", success.Value.GetProperty("stdout").GetProperty("text").GetString());
            Assert.False(success.Value.GetProperty("stdout").GetProperty("truncated").GetBoolean());
            Assert.Equal("hello\n", TextOf(result));
        }

        [Fact]
        public async Task NonZeroExit_IsReportedNotErrored()
        {
            var result = await Bash2("echo oops >&2; exit 3");
            Assert.False(result.IsError);
            Assert.Equal(3, Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("exitCode").GetInt32());
            var text = TextOf(result);
            Assert.Contains("[stderr]\noops\n", text);
            Assert.EndsWith("[exit code: 3]", text);
        }

        [Fact]
        public async Task Timeout_KillsAndReports()
        {
            var result = await Bash2("sleep 30", timeoutMs: 500);
            Assert.False(result.IsError);
            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.True(success.Value.GetProperty("timedOut").GetBoolean());
            Assert.Contains("[timed out after 500ms]", TextOf(result));
        }

        [Fact]
        public async Task LongOutput_IsTruncatedToTailWithSpill()
        {
            var result = await Bash2("seq 1 100000");
            Assert.False(result.IsError);
            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            var stdout = success.Value.GetProperty("stdout");
            Assert.True(stdout.GetProperty("truncated").GetBoolean());
            var text = stdout.GetProperty("text").GetString()!;
            Assert.DoesNotContain("1\n2\n", text);
            Assert.True(stdout.TryGetProperty("spillPath", out var spill));
            var spilled = File.ReadAllText(spill.GetString()!);
            Assert.StartsWith("1\n2\n", spilled);
            Assert.EndsWith("100000\n", spilled);
            Assert.Contains("[output truncated; full output:", TextOf(result));
        }
    }

    public sealed class Fs : IDisposable
    {
        private readonly ToolsTests _outer = new();

        public Fs()
        {
            ReadTool.Register(_outer._ctx);
            WriteTool.Register(_outer._ctx);
            EditTool.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        [Fact]
        public async Task WriteThenRead_RoundTripsLineNumbered()
        {
            var path = _outer.TempPath("note.txt");
            var write = await _outer.Execute("write", JsonSerializer.Serialize(new { file_path = path, content = "alpha\nbeta\n" }, DshJson.Options));
            Assert.False(write.IsError);
            var writeValue = Assert.IsType<ToolExecutionResult.Success>(write).Value;
            Assert.Equal("create", writeValue.GetProperty("operation").GetString());
            Assert.Contains("Created file", TextOf(write));

            var read = await _outer.Execute("read", JsonSerializer.Serialize(new { file_path = path }, DshJson.Options));
            Assert.False(read.IsError);
            var text = TextOf(read);
            Assert.Contains("1: alpha\n2: beta", text);
            Assert.EndsWith("(End of file - total 2 lines)\n</content>", TextOf(read));

            var second = await _outer.Execute("write", JsonSerializer.Serialize(new { file_path = path, content = "gamma" }, DshJson.Options));
            Assert.Equal("update", Assert.IsType<ToolExecutionResult.Success>(second).Value.GetProperty("operation").GetString());
            Assert.Equal("alpha\nbeta\n", Assert.IsType<ToolExecutionResult.Success>(second).Value.GetProperty("before").GetString());
        }

        [Fact]
        public async Task Read_OffsetAndLimit_PageTheFile()
        {
            var path = _outer.TempPath("paged.txt");
            File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 10)) + '\n');
            var read = await _outer.Execute("read", JsonSerializer.Serialize(new { file_path = path, offset = 4, limit = 3 }, DshJson.Options));
            var text = TextOf(read);
            Assert.Contains("4: 4\n5: 5\n6: 6", text);
            Assert.EndsWith("(Showing lines 4-6 of 10. Use offset=7 to continue.)\n</content>", text);
        }

        [Fact]
        public async Task Edit_UniqueReplacement_RoundTrips()
        {
            var path = _outer.TempPath("edit.txt");
            File.WriteAllText(path, "hello world\n");
            var edit = await _outer.Execute("edit", JsonSerializer.Serialize(new { file_path = path, old_string = "world", new_string = "harness" }, DshJson.Options));
            Assert.False(edit.IsError);
            Assert.Equal($"The file {path} has been updated successfully.", TextOf(edit));
            Assert.Equal("hello harness\n", File.ReadAllText(path));
        }

        [Fact]
        public async Task Edit_AmbiguousWithoutReplaceAll_Fails()
        {
            var path = _outer.TempPath("dupe.txt");
            File.WriteAllText(path, "x=1\nx=2\n");
            var edit = await _outer.Execute("edit", JsonSerializer.Serialize(new { file_path = path, old_string = "x", new_string = "y" }, DshJson.Options));
            Assert.True(edit.IsError);
            Assert.Contains("matched 2 times", TextOf(edit));
            Assert.Equal("x=1\nx=2\n", File.ReadAllText(path));

            var all = await _outer.Execute("edit", JsonSerializer.Serialize(new { file_path = path, old_string = "x", new_string = "y", replace_all = true }, DshJson.Options));
            Assert.False(all.IsError);
            Assert.Equal($"The file {path} has been updated. All occurrences were successfully replaced.", TextOf(all));
            Assert.Equal("y=1\ny=2\n", File.ReadAllText(path));
        }

        [Fact]
        public async Task Edit_MissingMatch_Fails()
        {
            var path = _outer.TempPath("nomatch.txt");
            File.WriteAllText(path, "content\n");
            var edit = await _outer.Execute("edit", JsonSerializer.Serialize(new { file_path = path, old_string = "absent", new_string = "x" }, DshJson.Options));
            Assert.True(edit.IsError);
            Assert.Contains("old_string was not found", TextOf(edit));
        }
    }

    public sealed class Search : IDisposable
    {
        private readonly ToolsTests _outer = new();

        public Search()
        {
            GlobTool.Register(_outer._ctx);
            GrepTool.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        private void SeedTree()
        {
            File.WriteAllText(_outer.TempPath("alpha.txt"), "needle one\nplain\n");
            File.WriteAllText(_outer.TempPath("notes.md"), "nothing here\n");
            Directory.CreateDirectory(_outer.TempPath("sub"));
            File.WriteAllText(_outer.TempPath("sub/beta.txt"), "needle two\n");
            Directory.CreateDirectory(_outer.TempPath(".git"));
            File.WriteAllText(_outer.TempPath(".git/ignored.txt"), "needle hidden\n");
        }

        [Fact]
        public async Task Glob_BasenamePatternMatchesAnyDepthAndSkipsVcs()
        {
            SeedTree();
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var old = _outer.TempPath("alpha.txt");
            var recent = _outer.TempPath("sub/beta.txt");
            File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(recent, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var result = await _outer.Execute("glob", JsonSerializer.Serialize(new { pattern = "*.txt", path = _outer._tempDir }, DshJson.Options), agent);
            Assert.False(result.IsError);
            var paths = Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("paths").EnumerateArray().Select(p => p.GetString()).ToList();
            Assert.Equal([Path.Combine("sub", "beta.txt").Replace('\\', '/'), "alpha.txt"], paths);
            Assert.DoesNotContain(paths, p => p!.Contains(".git"));
        }

        [Fact]
        public async Task Glob_NoMatches_RendersNoFilesFound()
        {
            SeedTree();
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("glob", JsonSerializer.Serialize(new { pattern = "*.csprojx", path = _outer._tempDir }, DshJson.Options), agent);
            Assert.Equal("No files found", TextOf(result));
        }

        [Fact]
        public async Task Grep_GroupsMatchesByFileWithLineNumbers()
        {
            SeedTree();
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("grep", JsonSerializer.Serialize(new { pattern = "needle", path = _outer._tempDir }, DshJson.Options), agent);
            Assert.False(result.IsError);
            var text = TextOf(result);
            Assert.StartsWith("Found 2 matches\n\n", text);
            Assert.Contains("alpha.txt\nLine 1: needle one", text);
            Assert.Contains($"{Path.Combine("sub", "beta.txt").Replace('\\', '/')}\nLine 1: needle two", text);
            Assert.DoesNotContain("ignored.txt", text);
        }

        [Fact]
        public async Task Grep_IncludeFilterExcludesNonMatchingFiles()
        {
            File.WriteAllText(_outer.TempPath("a.txt"), "needle\n");
            File.WriteAllText(_outer.TempPath("a.md"), "needle\n");
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("grep", JsonSerializer.Serialize(new { pattern = "needle", path = _outer._tempDir, include = "*.txt" }, DshJson.Options), agent);
            var text = TextOf(result);
            Assert.StartsWith("Found 1 match\n\n", text);
            Assert.Contains("a.txt\nLine 1: needle", text);
            Assert.DoesNotContain("a.md", text);
        }

        [Fact]
        public async Task Grep_NoMatches_RendersNoMatchesFound()
        {
            SeedTree();
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("grep", JsonSerializer.Serialize(new { pattern = "zzz-absent", path = _outer._tempDir }, DshJson.Options), agent);
            Assert.Equal("No matches found", TextOf(result));
        }
    }

    public sealed class Todo : IDisposable
    {
        private readonly ToolsTests _outer = new();

        public Todo()
        {
            TodoWriteTool.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        [Fact]
        public async Task TodoWrite_AppendsSnapshotAndRendersCounts()
        {
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("todo_write", """
                {"todos":[{"content":"plan","status":"completed"},{"content":"build","status":"in_progress"},{"content":"test","status":"pending"}]}
                """, agent);
            Assert.False(result.IsError);
            Assert.Equal("Updated todo list: 1 pending, 1 in progress, 1 completed.", TextOf(result));
            var payload = Assert.IsType<TodoWritePayload>(agent.Session.SnapshotEvents().Single().Data);
            Assert.Equal(["plan", "build", "test"], payload.Todos.Select(todo => todo.Content).ToList());
        }

        [Fact]
        public async Task TodoWrite_DuplicateContent_Fails()
        {
            var agent = new FakeAgent(_outer._ctx, _outer._tempDir);
            var result = await _outer.Execute("todo_write", """
                {"todos":[{"content":"same","status":"pending"},{"content":"same","status":"completed"}]}
                """, agent);
            Assert.True(result.IsError);
            Assert.Contains("duplicate content", TextOf(result));
            Assert.Empty(agent.Session.SnapshotEvents());
        }

        [Fact]
        public async Task TodoWrite_WithoutAgent_Fails()
        {
            var result = await _outer.Execute("todo_write", """
                {"todos":[{"content":"x","status":"pending"}]}
                """);
            Assert.True(result.IsError);
            Assert.Contains("requires an owning agent session", TextOf(result));
        }
    }
}
