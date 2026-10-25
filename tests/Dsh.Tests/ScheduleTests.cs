using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Goal;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Schedule;
using Microsoft.Extensions.Time.Testing;

namespace Dsh.Tests;

public static class ScheduleTests
{
    /** Thursday 2026-10-15 08:00 UTC. */
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static string CodeOf(Action action) => Assert.Throws<HarnessException>(action).Code;

    public sealed class Rules
    {
        [Fact]
        public void After_TargetsDelayFromNow()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.After(90), Start);
            Assert.Equal(ScheduleKind.After, rule.Kind);
            Assert.Equal(Start.AddSeconds(90), rule.ScheduledAt);
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.After(0), Start)));
        }

        [Fact]
        public void AtInstant_RequiresOffsetAndFutureTarget()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.AtInstant("2026-10-16T09:00:00+08:00"), Start);
            Assert.Equal(ScheduleKind.At, rule.Kind);
            Assert.Equal(Utc("2026-10-16T01:00:00Z"), rule.ScheduledAt);
            Assert.Equal(TimeSpan.Zero, rule.ScheduledAt.Offset);
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.AtInstant("2026-10-16T09:00:00"), Start)));
            Assert.Equal(ScheduleErrorCodes.NotFuture, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.AtInstant("2026-10-15T08:00:00Z"), Start)));
        }

        [Fact]
        public void AtLocal_RejectsDaylightSavingGapAndTakesEarlierOverlap()
        {
            var now = Utc("2026-01-01T00:00:00Z");
            Assert.Equal(
                ScheduleErrorCodes.InvalidRule,
                CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.AtLocal("2026-03-08", "02:30:00", "America/New_York"), now)));
            var overlap = ScheduleRules.Resolve(new ScheduleTiming.AtLocal("2026-11-01", "01:30:00", "America/New_York"), now);
            Assert.Equal(Utc("2026-11-01T05:30:00Z"), overlap.ScheduledAt);
            var plain = ScheduleRules.Resolve(new ScheduleTiming.AtLocal("2026-10-16", "09:00:00", "Asia/Shanghai"), Start);
            Assert.Equal(Utc("2026-10-16T01:00:00Z"), plain.ScheduledAt);
            Assert.Equal(
                ScheduleErrorCodes.InvalidTimeZone,
                CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.AtLocal("2026-10-16", "09:00:00", "Mars/Olympus"), Start)));
        }

        [Fact]
        public void Every_EnforcesMinimumAndDeliversLatestMissedOccurrence()
        {
            Assert.Equal(ScheduleErrorCodes.FrequencyTooHigh, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Every(59), Start)));
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Every(3600), Start);
            Assert.Equal(Start.AddHours(1), rule.ScheduledAt);

            var occurrence = ScheduleRules.Occur(rule, Start.AddHours(3.5));
            Assert.Equal(Start.AddHours(3), occurrence.OccurrenceAt);
            Assert.Equal(Start.AddHours(4), occurrence.NextAt);
        }

        [Fact]
        public void Daily_FollowsLocalTimeInItsZone()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("23:00:00", "Asia/Shanghai"), Utc("2026-10-15T15:30:00Z"));
            Assert.Equal("23:00:00.000", rule.Time);
            Assert.Equal(Utc("2026-10-16T15:00:00Z"), rule.ScheduledAt);

            var occurrence = ScheduleRules.Occur(rule, Utc("2026-10-19T00:00:00Z"));
            Assert.Equal(Utc("2026-10-18T15:00:00Z"), occurrence.OccurrenceAt);
            Assert.Equal(Utc("2026-10-19T15:00:00Z"), occurrence.NextAt);
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Daily("25:00:00", "UTC"), Start)));
        }

        [Fact]
        public void Daily_LocalTimeSkippedByDaylightSavingSkipsThatDate()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("02:30:00", "America/New_York"), Utc("2026-03-07T12:00:00Z"));
            Assert.Equal(Utc("2026-03-09T06:30:00Z"), rule.ScheduledAt);
        }

        [Fact]
        public void Weekly_UsesIsoWeekdaysIncludingSunday()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Weekly("09:00:00", "UTC", [7, 1]), Start);
            Assert.Equal([1, 7], rule.Weekdays);
            Assert.Equal(Utc("2026-10-18T09:00:00Z"), rule.ScheduledAt);
            Assert.Equal(Utc("2026-10-19T09:00:00Z"), ScheduleRules.Occur(rule, Utc("2026-10-18T10:00:00Z")).NextAt);
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Weekly("09:00:00", "UTC", [0]), Start)));
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Weekly("09:00:00", "UTC", [2, 2]), Start)));
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Weekly("09:00:00", "UTC", []), Start)));
        }

        [Fact]
        public void Cron_EvaluatesInZoneAndDeliversLatestMissedOccurrence()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Cron("*/15 9-17 * * 1-5", "Asia/Shanghai"), Start);
            Assert.Equal(Utc("2026-10-15T08:15:00Z"), rule.ScheduledAt);

            var occurrence = ScheduleRules.Occur(rule, Utc("2026-10-15T09:20:00Z"));
            Assert.Equal(Utc("2026-10-15T09:15:00Z"), occurrence.OccurrenceAt);
            Assert.Equal(Utc("2026-10-15T09:30:00Z"), occurrence.NextAt);
            Assert.Equal(ScheduleErrorCodes.InvalidRule, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Cron("0 9 * *", "UTC"), Start)));
            Assert.Equal(ScheduleErrorCodes.InvalidTimeZone, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Cron("0 9 * * *", "Nowhere/Zone"), Start)));
        }

        [Fact]
        public void Cron_RestrictedDayAndWeekdayUseVixieOr()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Cron("0 9 16 * 5", "UTC"), Start);
            Assert.Equal(Utc("2026-10-16T09:00:00Z"), rule.ScheduledAt);
            Assert.Equal(Utc("2026-10-23T09:00:00Z"), ScheduleRules.Occur(rule, Utc("2026-10-16T09:00:00Z")).NextAt);
        }

        [Fact]
        public void Daily_RepeatedLocalTimeFiresOnlyOnce()
        {
            var rule = ScheduleRules.Resolve(new ScheduleTiming.Daily("01:30:00", "America/New_York"), Utc("2026-10-31T12:00:00Z"));
            Assert.Equal(Utc("2026-11-01T05:30:00Z"), rule.ScheduledAt);
            var occurrence = ScheduleRules.Occur(rule, Utc("2026-11-01T06:45:00Z"));
            Assert.Equal(Utc("2026-11-01T05:30:00Z"), occurrence.OccurrenceAt);
            Assert.Equal(Utc("2026-11-02T06:30:00Z"), occurrence.NextAt);
        }

        [Fact]
        public void Cron_WithoutFutureMatchIsOutOfRange()
            => Assert.Equal(ScheduleErrorCodes.TimeOutOfRange, CodeOf(() => ScheduleRules.Resolve(new ScheduleTiming.Cron("0 0 30 2 *", "UTC"), Start)));
    }

    private sealed class RecordingAgent(Context ctx, Session session) : IAgent
    {
        public SessionId Id => Session.Id;
        public Session Session { get; } = session;
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; } = ctx;
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();
        public List<UserMessage> Followups { get; } = [];

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) => Followups.Add(message);
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _tools;

        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Schedules = new ScheduleService(Ctx, Time);
            _tools = ScheduleTools.Apply(Ctx);
        }

        public Context Ctx { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public ScheduleService Schedules { get; }

        public RecordingAgent Open(Session? session = null)
        {
            var agent = new RecordingAgent(Ctx, session ?? NewSession());
            Agents.Register(agent);
            return agent;
        }

        public async Task<JsonElement> Call(IAgent agent, string name, string arguments)
        {
            var result = await Execute(agent, name, arguments);
            return result is ToolExecutionResult.Success success
                ? success.Value
                : throw new Xunit.Sdk.XunitException($"{name} failed: {((ToolExecutionResult.Failure)result).Error.Message}");
        }

        public async Task<string?> FailureCode(IAgent agent, string name, string arguments)
            => (await Execute(agent, name, arguments)) is ToolExecutionResult.Failure failure ? failure.Error.Info?.Code : null;

        public void Dispose()
        {
            _tools.Dispose();
            Schedules.Dispose();
        }

        private Task<ToolExecutionResult> Execute(IAgent agent, string name, string arguments)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = name,
                Arguments = JsonDocument.Parse(arguments).RootElement,
                RawArguments = arguments,
                Agent = agent,
                Signal = TestContext.Current.CancellationToken,
            });
    }

    private static Session NewSession(int? delegationDepth = null, IReadOnlyList<SessionEvent>? seed = null)
    {
        var id = SessionId.Create($"session-{Guid.NewGuid():N}");
        return Session.Create(id, seed, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 0,
            IsSeeded = seed is not null,
            DelegationDepth = delegationDepth,
        }, seed?.Count);
    }

    private static string TextOf(UserMessage message) => string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));

    public sealed class Delivery
    {
        [Fact]
        public async Task OneShot_DeliversFramedFollowupOnceAndEnds()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            var created = await harness.Call(agent, "schedule_create", """{ "title": "部署检查", "prompt": "查看部署日志", "after_seconds": 60 }""");
            var id = created.GetProperty("id").GetString()!;
            Assert.Equal("scheduled", created.GetProperty("state").GetString());
            Assert.Equal("2026-10-15T08:01:00.000Z", created.GetProperty("scheduledAt").GetString());

            harness.Time.Advance(TimeSpan.FromSeconds(59));
            Assert.Empty(agent.Followups);
            harness.Time.Advance(TimeSpan.FromSeconds(1));

            var message = Assert.Single(agent.Followups);
            var source = Assert.IsType<PluginMessageSource>(message.Source);
            Assert.Equal(ScheduleService.MessagePlugin, source.Plugin);
            Assert.Equal(
                $"[SCHEDULE REMINDER]\n{ScheduleFraming.ScheduledMessageLine}\nschedule_id_json: \"{id}\"\noccurrence_at: 2026-10-15T08:01:00.000Z\nreminder_prompt_json: \"查看部署日志\"",
                TextOf(message));
            Assert.Equal(0, (await harness.Call(agent, "schedule_list", "{}")).GetArrayLength());
            var update = await harness.Call(agent, "schedule_update", $$"""{ "id": "{{id}}", "prompt": "again" }""");
            Assert.Equal(ScheduleErrorCodes.Ended, update.GetProperty("code").GetString());

            harness.Time.Advance(TimeSpan.FromHours(1));
            Assert.Single(agent.Followups);
        }

        [Fact]
        public async Task Recurring_SharesOneBatchAndDeliversOnlyLatestMissedOccurrence()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            await harness.Call(agent, "schedule_create", """{ "title": "a", "prompt": "tick", "every_seconds": 60 }""");
            await harness.Call(agent, "schedule_create", """{ "title": "b", "prompt": "daily", "daily": { "time": "08:02:00", "time_zone": "UTC" } }""");
            harness.Agents.Remove(agent.Id);
            harness.Time.Advance(TimeSpan.FromSeconds(150));
            Assert.Empty(agent.Followups);

            var resumed = harness.Open(Session.FromRestore(agent.Id, agent.Session.SnapshotEvents(), agent.Session.Header, 0));
            harness.Time.Advance(TimeSpan.Zero);

            var message = Assert.Single(resumed.Followups);
            var text = TextOf(message);
            Assert.StartsWith($"[SCHEDULE REMINDER BATCH]\n{ScheduleFraming.ScheduledMessageLine}\nreminders_json: ", text);
            var batch = JsonDocument.Parse(text[(text.IndexOf("reminders_json: ", StringComparison.Ordinal) + "reminders_json: ".Length)..]).RootElement;
            Assert.Equal(["2026-10-15T08:02:00.000Z", "2026-10-15T08:02:00.000Z"], batch.EnumerateArray().Select(item => item.GetProperty("occurrence_at").GetString()));
            var list = await harness.Call(resumed, "schedule_list", "{}");
            Assert.Equal(
                ["2026-10-15T08:03:00.000Z", "2026-10-16T08:02:00.000Z"],
                list.EnumerateArray().Select(view => view.GetProperty("scheduledAt").GetString()));
        }

        [Fact]
        public async Task ClosedSession_StopsTimersAndForkDoesNotInheritReminders()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            await harness.Call(agent, "schedule_create", """{ "title": "t", "prompt": "p", "after_seconds": 30 }""");
            var fork = harness.Open(NewSession(seed: agent.Session.SnapshotEvents()));
            harness.Agents.Remove(agent.Id);
            harness.Time.Advance(TimeSpan.FromMinutes(5));

            Assert.Empty(agent.Followups);
            Assert.Empty(fork.Followups);
            Assert.Equal(0, (await harness.Call(fork, "schedule_list", "{}")).GetArrayLength());
        }

        [Fact]
        public async Task Update_RetimesInPlaceAndDeleteStopsDelivery()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            var id = (await harness.Call(agent, "schedule_create", """{ "title": "t", "prompt": "p", "after_seconds": 30 }""")).GetProperty("id").GetString();
            var updated = await harness.Call(agent, "schedule_update", $$"""{ "id": "{{id}}", "title": "new", "at": "2026-10-15T09:00:00Z" }""");
            Assert.Equal(id, updated.GetProperty("id").GetString());
            Assert.Equal("at", updated.GetProperty("kind").GetString());
            Assert.Equal("new", updated.GetProperty("title").GetString());
            Assert.Equal("p", updated.GetProperty("prompt").GetString());

            harness.Time.Advance(TimeSpan.FromMinutes(30));
            Assert.Empty(agent.Followups);
            Assert.True((await harness.Call(agent, "schedule_delete", $$"""{ "id": "{{id}}" }""")).GetProperty("deleted").GetBoolean());
            harness.Time.Advance(TimeSpan.FromHours(1));
            Assert.Empty(agent.Followups);
            var missing = await harness.Call(agent, "schedule_delete", $$"""{ "id": "{{id}}" }""");
            Assert.Equal(ScheduleErrorCodes.NotFound, missing.GetProperty("code").GetString());
        }

        [Fact]
        public async Task Tools_RejectBadSelectorsAndSubagents()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            Assert.Equal(ScheduleErrorCodes.InvalidSelector, await harness.FailureCode(agent, "schedule_create", """{ "title": "t", "prompt": "p" }"""));
            Assert.Equal(
                ScheduleErrorCodes.InvalidSelector,
                await harness.FailureCode(agent, "schedule_create", """{ "title": "t", "prompt": "p", "after_seconds": 60, "every_seconds": 60 }"""));
            Assert.Equal(ScheduleErrorCodes.InvalidPrompt, await harness.FailureCode(agent, "schedule_create", """{ "title": " ", "prompt": "p", "after_seconds": 60 }"""));
            Assert.Equal(ScheduleErrorCodes.InvalidSelector, await harness.FailureCode(agent, "schedule_update", """{ "id": "x", "after_seconds": 60 }"""));
            Assert.Equal(ScheduleErrorCodes.InvalidSelector, await harness.FailureCode(agent, "schedule_update", """{ "id": "x" }"""));

            var child = harness.Open(NewSession(delegationDepth: 1));
            Assert.Equal(ScheduleErrorCodes.SubagentSession, await harness.FailureCode(child, "schedule_list", "{}"));
        }

        [Fact]
        public void ScheduleChanges_RoundTripThroughSessionPersistence()
        {
            using var harness = new Harness();
            var agent = harness.Open();
            harness.Schedules.Create(agent, "weekly sync", "prepare notes", new ScheduleTiming.Weekly("09:00:00", "Asia/Shanghai", [1, 5]));
            var directory = TempTree.CreateDirectory("dsh-schedule");
            try
            {
                using var persistence = new JsonlSessionPersistence(directory);
                using (var writer = persistence.Create(agent.Session.Header))
                {
                    writer.Append(agent.Session.SnapshotEvents());
                    writer.Close();
                }
                using var reader = persistence.Open(agent.Id, SessionAccess.Read);
                var restored = Session.FromRestore(agent.Id, reader.Read(), reader.Header, reader.InheritedEventCount);
                reader.Close();

                Assert.Equal(ScheduleLedger.Of(agent.Session).Active, ScheduleLedger.Of(restored).Active, RecordComparer.Instance);
            }
            finally
            {
                TempTree.Delete(directory);
            }
        }
    }

    /** A real agent loop with a scripted model: proves where reminders land relative to turns. */
    public sealed class AgentLoopDelivery
    {
        /** Bounds every wait so a stuck turn fails this test instead of hanging the whole run. */
        private static readonly TimeSpan TurnDeadline = TimeSpan.FromSeconds(5);

        private sealed class ScriptedAdapter : LlmAdapter
        {
            private readonly Queue<Func<Task<IReadOnlyList<StreamChunk>>>> _script = new();

            public override LlmProviderInfo ProviderInfo { get; } = new("test-provider", "test-provider");

            public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

            public List<GenerateOptions> Requests { get; } = [];

            public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Then(Func<Task<IReadOnlyList<StreamChunk>>> response) => _script.Enqueue(response);

            public void Then(IReadOnlyList<StreamChunk> response) => _script.Enqueue(() => Task.FromResult(response));

            public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
            {
                Requests.Add(options);
                FirstRequest.TrySetResult();
                return Yield(_script.Dequeue(), cancellationToken);
            }

            private static async IAsyncEnumerable<StreamChunk> Yield(
                Func<Task<IReadOnlyList<StreamChunk>>> response, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                foreach (var chunk in await response())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return chunk;
                    await Task.Yield();
                }
            }
        }

        private sealed class LoopFixture : IDisposable
        {
            private readonly IDisposable _scheduleTools;
            private readonly IDisposable _goalTools;

            public LoopFixture()
            {
                _ = new SessionStore(Ctx);
                _ = new SystemPrompt(Ctx, new SystemPromptConfig());
                _ = new ToolRuntime(Ctx);
                var llm = new LlmRuntime(Ctx);
                _ = new SessionProjectionRegistry(Ctx);
                Agents = new AgentRegistry(Ctx);
                _ = new AgentLoop(Ctx);
                _ = new GoalService(Ctx);
                _goalTools = GoalTools.Apply(Ctx);
                Schedules = new ScheduleService(Ctx, Time);
                _scheduleTools = ScheduleTools.Apply(Ctx);
                llm.RegisterAdapter(["test-provider"], Adapter);
            }

            public Context Ctx { get; } = new();
            public FakeTimeProvider Time { get; } = new(Start);
            public ScriptedAdapter Adapter { get; } = new();
            public AgentRegistry Agents { get; }
            public ScheduleService Schedules { get; }

            public async Task<AgentLoopAgent> Open()
            {
                var handle = await Agents.Create(new CreateAgentOptions(
                    SessionId.Create($"session-{Guid.NewGuid():N}"), null, new AgentOptions("test-provider", "test-model")));
                return (AgentLoopAgent)handle.Agent;
            }

            public void Dispose()
            {
                _scheduleTools.Dispose();
                _goalTools.Dispose();
                Schedules.Dispose();
            }
        }

        private static IReadOnlyList<StreamChunk> Text(string text) =>
        [
            new StreamChunk.BlockStart(0, "text"),
            new StreamChunk.TextDelta(0, text),
            new StreamChunk.BlockEnd(0, new TextBlock(text)),
            new StreamChunk.Finish(new FinishReason.Stop()),
        ];

        private static IReadOnlyList<StreamChunk> ToolCall(string name, string arguments) =>
        [
            new StreamChunk.ToolCallDelta(0, ToolCallId.Create($"call-{Guid.NewGuid():N}"), name, arguments),
            new StreamChunk.Finish(new FinishReason.ToolCalls()),
        ];

        private static bool CarriesReminder(GenerateOptions request)
            => request.Messages.Any(message => message.Content.OfType<TextBlock>().Any(block => block.Text.StartsWith("[SCHEDULE REMINDER]", StringComparison.Ordinal)));

        private static int TurnsOf(AgentLoopAgent agent) => agent.Session.SnapshotEvents().Count(sessionEvent => sessionEvent.Data is TurnStartPayload);

        [Fact]
        public async Task IdleSession_ReminderStartsNewTurn()
        {
            using var fixture = new LoopFixture();
            fixture.Adapter.Then(ToolCall("schedule_create", """{ "title": "check", "prompt": "check the build", "after_seconds": 30 }"""));
            fixture.Adapter.Then(Text("set"));
            fixture.Adapter.Then(Text("checked"));
            var agent = await fixture.Open();

            agent.Followup(MessageFactory.CreateUserText("remind me in 30 seconds"));
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            Assert.Equal(2, fixture.Adapter.Requests.Count);

            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            Assert.Equal(3, fixture.Adapter.Requests.Count);
            Assert.Equal(2, TurnsOf(agent));
            Assert.True(CarriesReminder(fixture.Adapter.Requests[2]));
        }

        [Fact]
        public async Task BusySession_ReminderWaitsForCurrentTurnToEnd()
        {
            using var fixture = new LoopFixture();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Adapter.Then(async () =>
            {
                await release.Task;
                return Text("long work done");
            });
            fixture.Adapter.Then(Text("reminder handled"));
            var agent = await fixture.Open();
            fixture.Schedules.Create(agent, "ping", "ping me", new ScheduleTiming.After(30));

            agent.Followup(MessageFactory.CreateUserText("long work"));
            await fixture.Adapter.FirstRequest.Task.WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.Single(fixture.Adapter.Requests);
            release.SetResult();
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            Assert.Equal(2, fixture.Adapter.Requests.Count);
            Assert.False(CarriesReminder(fixture.Adapter.Requests[0]));
            Assert.True(CarriesReminder(fixture.Adapter.Requests[1]));
            Assert.Equal(2, TurnsOf(agent));
        }

        [Fact]
        public async Task DeleteAfterDispatch_DoesNotRetractQueuedReminder()
        {
            using var fixture = new LoopFixture();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Adapter.Then(async () =>
            {
                await release.Task;
                return Text("done");
            });
            fixture.Adapter.Then(Text("reminder handled"));
            var agent = await fixture.Open();
            var record = fixture.Schedules.Create(agent, "ping", "ping me", new ScheduleTiming.After(30));
            agent.Followup(MessageFactory.CreateUserText("work"));
            await fixture.Adapter.FirstRequest.Task.WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));

            Assert.True(fixture.Schedules.Delete(agent, record.Id));
            release.SetResult();
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            Assert.Equal(2, fixture.Adapter.Requests.Count);
            Assert.True(CarriesReminder(fixture.Adapter.Requests[1]));
            Assert.Empty(fixture.Schedules.List(agent));
        }

        [Fact]
        public async Task UserInterrupt_DropsQueuedOneShotWithoutRedelivery()
        {
            using var fixture = new LoopFixture();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Adapter.Then(async () =>
            {
                await release.Task;
                return Text("cancelled");
            });
            fixture.Adapter.Then(Text("new user turn"));
            var agent = await fixture.Open();
            fixture.Schedules.Create(agent, "ping", "ping me", new ScheduleTiming.After(30));
            agent.Followup(MessageFactory.CreateUserText("work"));
            await fixture.Adapter.FirstRequest.Task.WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            agent.Cancel(new AgentCancelCause.User());
            release.SetResult();
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromMinutes(5));
            agent.Followup(MessageFactory.CreateUserText("continue"));
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            Assert.Equal(2, fixture.Adapter.Requests.Count);
            Assert.DoesNotContain(fixture.Adapter.Requests, CarriesReminder);
            Assert.Empty(fixture.Schedules.List(agent));
            Assert.Single(agent.Session.SnapshotEvents(), sessionEvent =>
                sessionEvent.Data is ScheduleChangePayload { Change.Operation: ScheduleOperation.Dispatch });
        }

        [Fact]
        public async Task ReminderTurn_GrantsNoGoalAuthority()
        {
            using var fixture = new LoopFixture();
            fixture.Adapter.Then(ToolCall("create_goal", """{ "objective": "ship it" }"""));
            fixture.Adapter.Then(Text("cannot"));
            var agent = await fixture.Open();
            fixture.Schedules.Create(agent, "goal", "create a goal to ship it", new ScheduleTiming.After(30));

            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            await agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            var result = agent.Session.SnapshotEvents().Select(sessionEvent => sessionEvent.Data).OfType<ToolResultPayload>().Single();
            Assert.Equal(GoalAuthority.AuthorityRequiredCode, result.Error?.Code);
            Assert.Null(fixture.Ctx.Get<GoalService>(GoalService.ServiceName)!.Get(agent));
        }
    }

    /** The composed harness: plugin-scoped service, system clock, and jsonl persistence across a cold resume. */
    public sealed class ComposedResume
    {
        private static readonly TimeSpan DeliveryDeadline = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ResumedSession_DeliversReminderThatWentOverdueWhileClosed(bool disableBeforeResume)
        {
            var directory = TempTree.CreateDirectory("dsh-schedule-resume");
            var home = Path.Combine(directory, "home");
            Directory.CreateDirectory(home);
            await File.WriteAllTextAsync(Path.Combine(home, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
            var sessionId = SessionId.Create($"session-{Guid.NewGuid():N}");
            var overdue = new ScheduleRecord("schedule-overdue", "check", "check the build", new ScheduleRule(ScheduleKind.At, DateTimeOffset.UtcNow.AddMinutes(-5)));
            try
            {
                using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(home), directory)))
                {
                    var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                    var created = await agents.Create(new CreateAgentOptions(sessionId, directory, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken);
                    created.Agent.Session.Append(new ScheduleChangePayload(new ScheduleChange(ScheduleOperation.Create, overdue)));
                    await app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(created.Agent.Session);
                }

                if (disableBeforeResume)
                {
                    await File.WriteAllTextAsync(Path.Combine(home, "settings.yaml"), GuiTestEnvironment.HomeSettings + """

                        plugins:
                          "@deepseek-ai/dsh-schedule": false
                          "@deepseek-ai/dsh-tool-schedule": false
                        """, TestContext.Current.CancellationToken);
                    using (var disabled = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(home), directory)))
                    {
                        Assert.Null(disabled.Ctx.Get<ScheduleService>(ScheduleService.ServiceName));
                        var agents = disabled.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                        var resumed = (await agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken)).Agent;
                        Assert.Single(ScheduleLedger.Of(resumed.Session).Active);
                        Assert.False(ReminderReachedModel(resumed.Session));
                        await disabled.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(resumed.Session);
                    }
                    await File.WriteAllTextAsync(Path.Combine(home, "settings.yaml"), GuiTestEnvironment.HomeSettings, TestContext.Current.CancellationToken);
                }

                using (var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(home), directory)))
                {
                    var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
                    var resumed = (await agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken)).Agent;
                    var deadline = DateTime.UtcNow + DeliveryDeadline;
                    while (!ReminderReachedModel(resumed.Session) && DateTime.UtcNow < deadline)
                        await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
                    await resumed.WhenIdle().WaitAsync(DeliveryDeadline, TestContext.Current.CancellationToken);

                    Assert.True(ReminderReachedModel(resumed.Session), "the overdue reminder did not start a turn after resume");
                    Assert.Contains(
                        resumed.Session.SnapshotEvents(),
                        sessionEvent => sessionEvent.Data is ScheduleChangePayload { Change: { Operation: ScheduleOperation.Dispatch, Id: "schedule-overdue" } });
                    Assert.Empty(ScheduleLedger.Of(resumed.Session).Active);
                }
            }
            finally
            {
                TempTree.Delete(directory);
            }
        }

        private static bool ReminderReachedModel(Session session)
            => session.SnapshotEvents().Any(sessionEvent
                => sessionEvent.Data is UserMessagePayload { Message.Source: PluginMessageSource { Plugin: ScheduleService.MessagePlugin } });
    }

    private sealed class RecordComparer : IEqualityComparer<ScheduleRecord>
    {
        public static readonly RecordComparer Instance = new();

        public bool Equals(ScheduleRecord? x, ScheduleRecord? y)
            => x is not null && y is not null
                && x with { Rule = x.Rule with { Weekdays = null } } == y with { Rule = y.Rule with { Weekdays = null } }
                && (x.Rule.Weekdays ?? []).SequenceEqual(y.Rule.Weekdays ?? []);

        public int GetHashCode(ScheduleRecord obj) => obj.Id.GetHashCode();
    }
}
