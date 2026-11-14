using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Schedule;
using Microsoft.Extensions.Time.Testing;

namespace Dsh.Tests;

public sealed class ScheduleActivationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TurnDeadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivatedAfterAgentRegistration_DeliversExistingAndNewReminders(bool existingReminder)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        _ = new SessionProjectionRegistry(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var handle = await agents.Create(new CreateAgentOptions(), TestContext.Current.CancellationToken);
        using var ownership = handle.Dispose;
        var time = new FakeTimeProvider(Start);
        var timing = new ScheduleTiming.After(30);
        var record = new ScheduleRecord("schedule-before-activation", "check", "check the build", ScheduleRules.Resolve(timing, Start));
        if (existingReminder)
        {
            handle.Agent.Session.Append(new ScheduleChangePayload(new ScheduleChange(ScheduleOperation.Create, record)));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        using var schedules = new ScheduleService(ctx, time);
        if (!existingReminder)
            record = schedules.Create(handle.Agent, record.Title, record.Prompt, timing);
        time.Advance(TimeSpan.FromSeconds(30));
        await handle.Agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

        var reminder = Assert.Single(handle.Agent.Session.SnapshotEvents().Select(item => item.Data).OfType<UserMessagePayload>(),
            payload => payload.Message.Source is PluginMessageSource { Plugin: ScheduleService.MessagePlugin });
        Assert.Contains(record.Id, Assert.Single(reminder.Message.Content.OfType<TextBlock>()).Text);
        Assert.Empty(schedules.List(handle.Agent));
        var dispatch = Assert.Single(handle.Agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ScheduleChangePayload>(),
            payload => payload.Change.Operation == ScheduleOperation.Dispatch);
        Assert.Equal(record.Id, dispatch.Change.Id);
        Assert.Equal(record.Rule.ScheduledAt, dispatch.Change.OccurrenceAt);
    }

    [Fact]
    public async Task DisposedDuringActivation_IsNotRearmedFromRegistrySnapshot()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        _ = new SessionProjectionRegistry(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var first = await agents.Create(new CreateAgentOptions(), TestContext.Current.CancellationToken);
        using var firstOwnership = first.Dispose;
        var second = await agents.Create(new CreateAgentOptions(), TestContext.Current.CancellationToken);
        using var secondOwnership = second.Dispose;
        foreach (var handle in new[] { first, second })
            handle.Agent.Session.Append(new ScheduleChangePayload(new ScheduleChange(ScheduleOperation.Create,
                new ScheduleRecord($"schedule-{handle.Agent.Id.Value}", "check", "check the build", new ScheduleRule(ScheduleKind.At, Start)))));
        var unsubscribe = ctx.On<AgentInboxInsertedNotification>(notification =>
        {
            if (ReferenceEquals(notification.Agent, first.Agent))
                second.Dispose.Dispose();
        }, new EventOptions { Global = true });
        try
        {
            using var schedules = new ScheduleService(ctx, new FakeTimeProvider(Start.AddMinutes(1)));
            await first.Agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);
            await second.Agent.WhenIdle().WaitAsync(TurnDeadline, TestContext.Current.CancellationToken);

            Assert.Null(agents.Get(second.Agent.Id));
            Assert.Single(first.Agent.Session.SnapshotEvents(), item => item.Data is UserMessagePayload);
            Assert.DoesNotContain(second.Agent.Session.SnapshotEvents(), item => item.Data is UserMessagePayload
                or ScheduleChangePayload { Change.Operation: ScheduleOperation.Dispatch });
            Assert.Single(ScheduleLedger.Of(second.Agent.Session).Active);
        }
        finally
        {
            unsubscribe();
        }
    }
}
