using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Tests;

public class JobsTests : IDisposable
{
    private readonly Context _ctx;
    private readonly ToolRuntime _tools;
    private readonly SubprocessService _subprocess;
    private readonly AgentRegistry _agents;
    private readonly LocalJobsService _jobs;
    private readonly string _tempDir;

    public JobsTests()
    {
        _ctx = new Context();
        _ = new SystemPrompt(_ctx, new SystemPromptConfig());
        _tools = new ToolRuntime(_ctx);
        _subprocess = new SubprocessService(_ctx);
        _agents = new AgentRegistry(_ctx);
        _jobs = new LocalJobsService(_ctx);
        _tempDir = Path.Combine(Path.GetTempPath(), $"dsh-jobs-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _subprocess.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx, string directory, AgentRegistry agents, AgentStatus status = AgentStatus.Idle)
        {
            Ctx = ctx;
            Status = status;
            _agents = agents;
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

        private readonly AgentRegistry _agents;

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status { get; }
        public AgentOptions Options { get; } = new();
        public List<UserMessage> Injected { get; } = [];
        public List<UserMessage> Followups { get; } = [];

        public void Register() => _agents.Register(this);
        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) => Followups.Add(message);
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) => Injected.Add(message);
    }

    private string StartBashJob(IAgent? owner, string command, int? outputLimitBytes = null)
        => _jobs.Start(new JobStart
        {
            Kind = "bash",
            Label = command,
            OutputLimitBytes = outputLimitBytes,
            Owner = owner,
            Run = () =>
            {
                var handle = _subprocess.Spawn(new SubprocessSpawnSpec
                {
                    Argv = ["bash", "-c", command],
                    Cwd = _tempDir,
                });
                long cursor = 0;
                return new JobHooks(
                    Cancel: _ => handle.Terminate(),
                    Done: AwaitDone(handle),
                    ReadOutput: () =>
                    {
                        var read = handle.StdoutReader.ReadFrom(cursor);
                        cursor = read.NextOffset;
                        return read.Text;
                    });
            },
        });

    private static async Task<JobOutcome> AwaitDone(SubprocessHandle handle)
    {
        var outcome = await handle.Done;
        if (outcome.Signal is not null)
            return new JobOutcome(JobStatus.Killed, Detail: $"signal: {outcome.Signal}");
        return outcome.ExitCode is 0 or null
            ? new JobOutcome(JobStatus.Completed, Detail: "exit code: 0")
            : new JobOutcome(JobStatus.Failed, Detail: $"exit code: {outcome.ExitCode}");
    }

    private Task<ToolExecutionResult> Execute(string name, object arguments, IAgent? agent = null)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments, DshJson.Options),
            Agent = agent,
            Signal = default,
        });

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private FakeAgent NewAgent(AgentStatus status = AgentStatus.Idle)
    {
        var agent = new FakeAgent(_ctx, _tempDir, _agents, status);
        agent.Register();
        return agent;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
    }

    public sealed class Lifecycle : IDisposable
    {
        private readonly JobsTests _outer = new();

        public Lifecycle()
        {
            ToolJobs.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        [Fact]
        public async Task Spawn_Output_Wait_SettlesCompleted()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "echo hello; echo world");
            Assert.Equal("bash-1", id);

            var result = await _outer.Execute("job_output", new { job_id = id, wait = true }, agent);
            Assert.False(result.IsError);
            var success = Assert.IsType<ToolExecutionResult.Success>(result);
            Assert.Equal("hello\nworld\n", success.Value.GetProperty("text").GetString());
            Assert.Equal("completed", success.Value.GetProperty("job").GetProperty("status").GetString());
            Assert.EndsWith("[status: completed, exit code: 0]", TextOf(result));
        }

        [Fact]
        public async Task StreamRead_ConsumesDeltas()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "echo first; sleep 1; echo second");
            await Task.Delay(500);

            var first = await _outer.Execute("job_output", new { job_id = id }, agent);
            Assert.Equal("first\n", Assert.IsType<ToolExecutionResult.Success>(first).Value.GetProperty("text").GetString());
            Assert.Equal("running", Assert.IsType<ToolExecutionResult.Success>(first).Value.GetProperty("job").GetProperty("status").GetString());

            var second = await _outer.Execute("job_output", new { job_id = id, wait = true }, agent);
            Assert.Equal("second\n", Assert.IsType<ToolExecutionResult.Success>(second).Value.GetProperty("text").GetString());
        }

        [Fact]
        public async Task Kill_RunningJob_SettlesKilled()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "sleep 30");

            var kill = await _outer.Execute("job_kill", new { job_id = id, reason = "no longer needed" }, agent);
            Assert.False(kill.IsError);
            var value = Assert.IsType<ToolExecutionResult.Success>(kill).Value;
            Assert.Equal("cancellation-requested", value.GetProperty("outcome").GetString());
            // TS 单线程下此处必为 stopping;.NET 下进程终止的 settle 可能在另一线程抢先提交 killed。
            var status = value.GetProperty("job").GetProperty("status").GetString()!;
            Assert.True(status is "stopping" or "killed", $"unexpected status {status}");
            Assert.Equal($"requested cancellation of job {id}", TextOf(kill));

            var settled = await _outer._jobs.WaitAsync(id, 5000, agent);
            Assert.Equal(JobStatus.Killed, settled.Status);

            var again = await _outer.Execute("job_kill", new { job_id = id }, agent);
            Assert.Equal("already-finished", Assert.IsType<ToolExecutionResult.Success>(again).Value.GetProperty("outcome").GetString());
            Assert.Contains($"job {id} had already finished", TextOf(again));
        }

        [Fact]
        public async Task WaitTimeout_ReturnsRunningSnapshot()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "sleep 30");
            var result = await _outer.Execute("job_output", new { job_id = id, wait = true, timeout_ms = 200 }, agent);
            Assert.False(result.IsError);
            Assert.Equal("running", Assert.IsType<ToolExecutionResult.Success>(result).Value.GetProperty("job").GetProperty("status").GetString());
            _outer._jobs.Kill(id, agent);
        }

        [Fact]
        public async Task JobList_RendersRegisteredJobs()
        {
            var agent = _outer.NewAgent();
            _outer.StartBashJob(agent, "sleep 30");
            var done = _outer.StartBashJob(agent, "true");
            await _outer._jobs.WaitAsync(done, 5000, agent);

            var result = await _outer.Execute("job_list", new { }, agent);
            Assert.False(result.IsError);
            var text = TextOf(result);
            Assert.Contains("bash-1 [bash] running — sleep 30", text);
            Assert.Contains("bash-2 [bash] completed — true", text);
            _outer._jobs.Kill("bash-1", agent);
        }

        [Fact]
        public async Task JobList_Empty_RendersNone()
        {
            var agent = _outer.NewAgent();
            var result = await _outer.Execute("job_list", new { }, agent);
            Assert.Equal("(no background jobs)", TextOf(result));
        }

        [Fact]
        public async Task UnknownJob_Errors()
        {
            var agent = _outer.NewAgent();
            var result = await _outer.Execute("job_output", new { job_id = "bash-99" }, agent);
            Assert.True(result.IsError);
            Assert.Contains("unknown job bash-99", TextOf(result));
        }

        [Fact]
        public async Task CompletionNotice_InjectsIntoBusyOwner()
        {
            var agent = _outer.NewAgent(AgentStatus.Running);
            var id = _outer.StartBashJob(agent, "sleep 0.3; echo done");
            await _outer._jobs.WaitAsync(id, 5000, agent);
            WaitFor(() => agent.Injected.Count == 0 && agent.Followups.Count == 0);

            var unread = _outer.StartBashJob(agent, "echo later");
            WaitFor(() => agent.Injected.Count > 0);
            await _outer._jobs.WaitAsync(unread, 5000, agent);
            var notice = Assert.Single(agent.Injected);
            var text = notice.Content.OfType<TextBlock>().Single().Text;
            Assert.Contains($"background job {unread}", text);
            Assert.Contains("finished [status: completed, exit code: 0]", text);
            var source = Assert.IsType<PluginMessageSource>(notice.Source);
            Assert.Equal("tool-jobs", source.Plugin);
            Assert.Equal(ContextForms.Notice, source.Form);
        }

        [Fact]
        public async Task CompletionNotice_WakesIdleOwner()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "sleep 0.3; echo done");
            await _outer._jobs.WaitAsync(id, 5000, agent);
            WaitFor(() => agent.Injected.Count == 0 && agent.Followups.Count == 0);

            var unread = _outer.StartBashJob(agent, "echo later");
            WaitFor(() => agent.Followups.Count > 0);
            await _outer._jobs.WaitAsync(unread, 5000, agent);
            Assert.Single(agent.Followups);
        }
    }

    public sealed class Isolation : IDisposable
    {
        private readonly JobsTests _outer = new();

        public Isolation()
        {
            ToolJobs.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        [Fact]
        public async Task Owners_SeeOnlyOwnJobs()
        {
            var alice = _outer.NewAgent();
            var bob = _outer.NewAgent();
            var aliceJob = _outer.StartBashJob(alice, "sleep 30");
            var bobJob = _outer.StartBashJob(bob, "sleep 30");
            _outer.StartBashJob(null, "sleep 30");

            var aliceList = TextOf(await _outer.Execute("job_list", new { }, alice));
            Assert.Contains(aliceJob, aliceList);
            Assert.DoesNotContain(bobJob, aliceList);
            Assert.Contains("bash-3", aliceList);

            var foreign = await _outer.Execute("job_output", new { job_id = bobJob }, alice);
            Assert.True(foreign.IsError);
            Assert.Contains("belongs to another session", TextOf(foreign));

            var foreignKill = await _outer.Execute("job_kill", new { job_id = bobJob }, alice);
            Assert.True(foreignKill.IsError);
            Assert.Contains("belongs to another session", TextOf(foreignKill));

            _outer._jobs.Kill(aliceJob, alice);
            _outer._jobs.Kill(bobJob, bob);
        }

        [Fact]
        public void Start_WithoutController_Refused()
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var jobs = new LocalJobsService(ctx);
            var error = Assert.Throws<InvalidOperationException>(() => jobs.Start(new JobStart
            {
                Kind = "bash",
                Label = "x",
                Run = () => new JobHooks(_ => { }, Task.FromResult(new JobOutcome(JobStatus.Completed))),
            }));
            Assert.Contains("no job controller serves this agent", error.Message);
        }

        [Fact]
        public void PerOwnerLimit_EnforcedIndependently()
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var jobs = new LocalJobsService(ctx, new LocalJobsConfig { MaxConcurrentJobsPerOwner = 2 });
            var agents = new AgentRegistry(ctx);
            jobs.AttachController("test");
            var agentA = new FakeAgent(ctx, _outer._tempDir, agents);
            agentA.Register();
            var agentB = new FakeAgent(ctx, _outer._tempDir, agents);
            agentB.Register();

            JobStart Spec(IAgent? owner, string label) => new()
            {
                Kind = "bash",
                Label = label,
                Owner = owner,
                Run = () => new JobHooks(_ => { }, new TaskCompletionSource<JobOutcome>().Task),
            };

            jobs.Start(Spec(agentA, "a1"));
            jobs.Start(Spec(agentA, "a2"));
            var error = Assert.Throws<InvalidOperationException>(() => jobs.Start(Spec(agentA, "a3")));
            Assert.Contains("background job limit reached", error.Message);
            jobs.Start(Spec(agentB, "b1"));
        }
    }

    public sealed class OutputLimit : IDisposable
    {
        private readonly JobsTests _outer = new();

        public OutputLimit()
        {
            ToolJobs.Register(_outer._ctx);
        }

        public void Dispose() => _outer.Dispose();

        [Fact]
        public async Task JobOutput_RespectsOutputLimitBytes()
        {
            var agent = _outer.NewAgent();
            var id = _outer.StartBashJob(agent, "seq 1 500", outputLimitBytes: 128);
            var result = await _outer.Execute("job_output", new { job_id = id, wait = true }, agent);
            Assert.False(result.IsError);
            var text = TextOf(result);
            Assert.Contains("[output truncated]", text);
            Assert.Contains("[status: completed", text);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 128, $"expected <= 128 bytes, got {System.Text.Encoding.UTF8.GetByteCount(text)}");
        }
    }

    public sealed class PersistentBash : IDisposable
    {
        private readonly JobsTests _outer = new();

        public PersistentBash()
        {
            PersistentBashTool.Register(_outer._ctx, new PersistentBashConfig { TimeoutMs = 10_000 });
        }

        public void Dispose() => _outer.Dispose();

        private bool BashAvailable()
        {
            try
            {
                new BashResolver(_outer._subprocess, null).Resolve(default);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ProbeBash(SubprocessService subprocess)
        {
            try
            {
                new BashResolver(subprocess, null).Resolve(default);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private Task<ToolExecutionResult> Bash(IAgent agent, string command)
            => _outer.Execute("bash", new { command }, agent);

        [Fact]
        public async Task CwdAndEnvironment_PersistAcrossCalls()
        {
            if (!BashAvailable()) return;
            Directory.CreateDirectory(Path.Combine(_outer._tempDir, "sub"));
            var agent = _outer.NewAgent();

            var cd = await Bash(agent, "cd sub && pwd");
            Assert.False(cd.IsError);
            Assert.Contains("sub", TextOf(cd));

            var pwd = await Bash(agent, "pwd");
            Assert.Contains("sub", TextOf(pwd));

            await Bash(agent, "export DSH_TEST_MARKER=hello");
            var echo = await Bash(agent, "echo $DSH_TEST_MARKER");
            Assert.Contains("hello", TextOf(echo));
        }

        [Fact]
        public async Task NonZeroExit_ReportedWithMarker()
        {
            if (!BashAvailable()) return;
            var agent = _outer.NewAgent();
            var result = await Bash(agent, "echo oops; false");
            Assert.False(result.IsError);
            var text = TextOf(result);
            Assert.Contains("oops", text);
            Assert.EndsWith("[exit code: 1]", text);
        }

        [Fact]
        public async Task Stderr_IsMergedIntoOutput()
        {
            if (!BashAvailable()) return;
            var agent = _outer.NewAgent();
            var result = await Bash(agent, "echo errline >&2");
            Assert.False(result.IsError);
            Assert.Contains("errline", TextOf(result));
        }

        [Fact]
        public async Task LongOutput_IsTruncated()
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            var subprocess = new SubprocessService(ctx);
            var agents = new AgentRegistry(ctx);
            if (!ProbeBash(subprocess)) return;
            PersistentBashTool.Register(ctx, new PersistentBashConfig { MaxOutputChars = 200, TimeoutMs = 10_000 });
            var agent = new FakeAgent(ctx, _outer._tempDir, agents);
            agent.Register();

            var result = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "bash",
                Arguments = JsonSerializer.SerializeToElement(new { command = "seq 1 1000" }, DshJson.Options),
                Agent = agent,
                Signal = default,
            });
            Assert.False(result.IsError);
            var text = TextOf(result);
            Assert.Contains("<response clipped>", text);
            Assert.DoesNotContain("1000", text);
        }

        [Fact]
        public async Task ShellExit_ResetsAndNextCallStartsFresh()
        {
            if (!BashAvailable()) return;
            var agent = _outer.NewAgent();
            await Bash(agent, "cd sub 2>/dev/null || true");
            var exited = await Bash(agent, "exit 3");
            Assert.False(exited.IsError);
            var text = TextOf(exited);
            Assert.Contains("[shell exited: code 3]", text);
            Assert.Contains("persistent bash shell was reset", text);

            var pwd = await Bash(agent, "pwd");
            Assert.False(pwd.IsError);
            Assert.DoesNotContain("sub", TextOf(pwd));
        }

        [Fact]
        public async Task Timeout_ResetsShell()
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var tools = new ToolRuntime(ctx);
            var subprocess = new SubprocessService(ctx);
            var agents = new AgentRegistry(ctx);
            if (!ProbeBash(subprocess)) return;
            PersistentBashTool.Register(ctx, new PersistentBashConfig { TimeoutMs = 500 });
            var agent = new FakeAgent(ctx, _outer._tempDir, agents);
            agent.Register();

            var slow = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "bash",
                Arguments = JsonSerializer.SerializeToElement(new { command = "sleep 30" }, DshJson.Options),
                Agent = agent,
                Signal = default,
            });
            Assert.False(slow.IsError);
            var text = TextOf(slow);
            Assert.Contains("timed out after 1 seconds", text);
            Assert.Contains("persistent bash shell was reset", text);

            var alive = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "bash",
                Arguments = JsonSerializer.SerializeToElement(new { command = "echo alive" }, DshJson.Options),
                Agent = agent,
                Signal = default,
            });
            Assert.False(alive.IsError);
            Assert.Contains("alive", TextOf(alive));
        }
    }
}
