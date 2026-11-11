using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Schedule;
using Microsoft.Extensions.Time.Testing;
using Dsh.Agent;

namespace Dsh.Tests;

/** Compatibility checks dispatch through the registered tools and the real session schedule service. */
public sealed class ScheduleToolCompatibilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _registration;
        private readonly ScheduleService _schedules;
        private readonly ToolRuntime _tools;
        private readonly Task<AgentHandle> _agent;

        public Fixture()
        {
            var ctx = new Context();
            _ = new SessionStore(ctx);
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            _tools = new ToolRuntime(ctx);
            _ = new LlmRuntime(ctx);
            _ = new SessionProjectionRegistry(ctx);
            var agents = new AgentRegistry(ctx);
            _ = new AgentLoop(ctx);
            _schedules = new ScheduleService(ctx, new FakeTimeProvider(Start));
            _registration = ScheduleTools.Apply(ctx);
            _agent = agents.Create(new CreateAgentOptions(SessionId.Create($"session-{Guid.NewGuid():N}")),
                TestContext.Current.CancellationToken);
        }

        public async Task<ToolExecutionResult> Execute(string name, JsonObject arguments)
        {
            var json = arguments.ToJsonString();
            using var document = JsonDocument.Parse(json);
            return await _tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = name,
                Arguments = document.RootElement,
                RawArguments = json,
                Agent = (await _agent).Agent,
                Signal = TestContext.Current.CancellationToken,
            });
        }

        public async Task<JsonElement> Success(string name, JsonObject arguments)
            => Assert.IsType<ToolExecutionResult.Success>(await Execute(name, arguments)).Value;

        public async Task RejectCreateAndUpdate(string selector, string code)
        {
            var timing = JsonNode.Parse(selector)!.AsObject();
            var create = new JsonObject { ["title"] = "check", ["prompt"] = "check the build" };
            foreach (var property in timing)
                create[property.Key] = property.Value?.DeepClone();
            var failure = Assert.IsType<ToolExecutionResult.Failure>(await Execute("schedule_create", create));
            Assert.Equal(code, failure.Error.Info?.Code);

            var existing = await Success("schedule_create", new JsonObject
            {
                ["title"] = "check", ["prompt"] = "check the build", ["after_seconds"] = 3600,
            });
            var update = new JsonObject { ["id"] = existing.GetProperty("id").GetString() };
            foreach (var property in timing)
                update[property.Key] = property.Value?.DeepClone();
            failure = Assert.IsType<ToolExecutionResult.Failure>(await Execute("schedule_update", update));
            Assert.Equal(code, failure.Error.Info?.Code);
        }

        public void Dispose()
        {
            _registration.Dispose();
            _schedules.Dispose();
            _agent.GetAwaiter().GetResult().Dispose.Dispose();
        }
    }

    [Theory]
    [InlineData("{\"at\":{\"date\":\"2026-10-16\",\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"extra\":true}}")]
    [InlineData("{\"daily\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"extra\":true}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[1],\"extra\":true}}")]
    [InlineData("{\"cron\":{\"expression\":\"0 9 * * *\",\"time_zone\":\"UTC\",\"extra\":true}}")]
    [InlineData("{\"at\":{\"date\":\"2026-10-16\",\"time\":\"09:00:00\"}}")]
    [InlineData("{\"daily\":{\"time\":\"09:00:00\"}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\"}}")]
    [InlineData("{\"cron\":{\"expression\":\"0 9 * * *\"}}")]
    [InlineData("{\"at\":null}")]
    [InlineData("{\"at\":42}")]
    [InlineData("{\"at\":[]}")]
    [InlineData("{\"daily\":null}")]
    [InlineData("{\"daily\":\"09:00:00\"}")]
    [InlineData("{\"weekly\":[]}")]
    [InlineData("{\"cron\":false}")]
    [InlineData("{\"at\":{\"date\":20261016,\"time\":\"09:00:00\",\"time_zone\":\"UTC\"}}")]
    [InlineData("{\"daily\":{\"time\":900,\"time_zone\":\"UTC\"}}")]
    [InlineData("{\"weekly\":{\"time\":false,\"time_zone\":\"UTC\",\"weekdays\":[1]}}")]
    [InlineData("{\"cron\":{\"expression\":5,\"time_zone\":\"UTC\"}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[\"1\"]}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[true]}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[1.5]}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[1,1]}}")]
    [InlineData("{\"weekly\":{\"time\":\"09:00:00\",\"time_zone\":\"UTC\",\"weekdays\":[]}}")]
    public async Task SelectorShapes_RejectWithInvalidRule(string selector)
    {
        using var fixture = new Fixture();
        await fixture.RejectCreateAndUpdate(selector, "invalid_rule");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"Eastern Standard Time\"")]
    [InlineData("\"+08:00\"")]
    [InlineData("\"GMT\"")]
    [InlineData("\" UTC\"")]
    [InlineData("\"UTC \"")]
    [InlineData("\"Mars/Olympus\"")]
    public async Task InvalidZoneKindsAndNames_UseInvalidTimeZone(string zone)
    {
        using var fixture = new Fixture();
        foreach (var selector in new[]
        {
            $$"""{"at":{"date":"2026-10-16","time":"09:00:00","time_zone":{{zone}} } }""",
            $$"""{"daily":{"time":"09:00:00","time_zone":{{zone}} } }""",
            $$"""{"weekly":{"time":"09:00:00","weekdays":[1],"time_zone":{{zone}} } }""",
            $$"""{"cron":{"expression":"0 9 * * *","time_zone":{{zone}} } }""",
        })
            await fixture.RejectCreateAndUpdate(selector, "invalid_time_zone");
    }

    [Theory]
    [InlineData("09:00")]
    [InlineData("9:00:00")]
    [InlineData("09:00:00.")]
    [InlineData("09:00:00.1234")]
    [InlineData("24:00:00")]
    [InlineData("09:00:60")]
    [InlineData(" 09:00:00")]
    [InlineData("09:00:00 ")]
    public async Task LocalClockForms_AreNotRelaxedOrTrimmed(string time)
    {
        using var fixture = new Fixture();
        foreach (var selector in new[]
        {
            $$"""{"at":{"date":"2026-10-16","time":"{{time}}","time_zone":"UTC"} }""",
            $$"""{"daily":{"time":"{{time}}","time_zone":"UTC"} }""",
            $$"""{"weekly":{"time":"{{time}}","time_zone":"UTC","weekdays":[1]} }""",
        })
            await fixture.RejectCreateAndUpdate(selector, "invalid_rule");
    }

    [Theory]
    [InlineData("2026-10-16t09:00:00Z")]
    [InlineData("2026-10-16T09:00:00z")]
    [InlineData("2026-10-16T09:00Z")]
    [InlineData("2026-10-16T09:00:00")]
    [InlineData("2026-10-16T09:00:00.1234Z")]
    [InlineData("2026-10-16T09:00:00-00:00")]
    [InlineData("2026-10-16T09:00:00+24:00")]
    [InlineData("2026-10-16T09:00:00+00:60")]
    [InlineData("2026-10-16T09:00:60Z")]
    [InlineData("2026-02-30T09:00:00Z")]
    [InlineData(" 2026-10-16T09:00:00Z")]
    [InlineData("2026-10-16T09:00:00Z ")]
    public async Task OffsetForms_UseStrictUpstreamProfile(string at)
    {
        using var fixture = new Fixture();
        await fixture.RejectCreateAndUpdate(new JsonObject { ["at"] = at }.ToJsonString(), "invalid_rule");
    }

    [Theory]
    [InlineData("2026-10-16T09:00:00.1+23:59", "2026-10-15T09:01:00.100Z")]
    [InlineData("2026-10-16T09:00:00.12-23:59", "2026-10-17T08:59:00.120Z")]
    [InlineData("2026-10-16T09:00:00.123+00:00", "2026-10-16T09:00:00.123Z")]
    public async Task WideOffsetsAndFractions_ArePreserved(string at, string expected)
    {
        using var fixture = new Fixture();
        var result = await fixture.Success("schedule_create", new JsonObject
        {
            ["title"] = "check", ["prompt"] = "check", ["at"] = at,
        });
        Assert.Equal(expected, result.GetProperty("scheduledAt").GetString());
    }

    [Theory]
    [InlineData("@daily")]
    [InlineData("0 9 * JAN MON")]
    [InlineData("0 9 ? * 1")]
    [InlineData("0 9 L * *")]
    [InlineData("0 9 1W * *")]
    [InlineData("0 9 * * 1#2")]
    [InlineData("0 0 9 * * *")]
    [InlineData("0 9 * * 5-1")]
    [InlineData("1/2 9 * * *")]
    [InlineData("*/0 9 * * *")]
    [InlineData("0 9 * * * ")]
    [InlineData(" 0 9 * * *")]
    public async Task UnsupportedCronDialects_AreNotAccepted(string expression)
    {
        using var fixture = new Fixture();
        await fixture.RejectCreateAndUpdate(new JsonObject
        {
            ["cron"] = new JsonObject { ["expression"] = expression, ["time_zone"] = "UTC" },
        }.ToJsonString(), "invalid_rule");
    }

    [Theory]
    [InlineData("{\"every_seconds\":null}", "invalid_rule")]
    [InlineData("{\"every_seconds\":\"60\"}", "invalid_rule")]
    [InlineData("{\"every_seconds\":true}", "invalid_rule")]
    [InlineData("{\"every_seconds\":60.5}", "invalid_rule")]
    [InlineData("{\"every_seconds\":9007199254740992}", "invalid_rule")]
    [InlineData("{\"every_seconds\":59}", "frequency_too_high")]
    [InlineData("{\"every_seconds\":-1}", "frequency_too_high")]
    [InlineData("{\"every_seconds\":9007199254740991}", "time_out_of_range")]
    [InlineData("{\"daily\":null,\"every_seconds\":60}", "invalid_selector")]
    [InlineData("{\"every_seconds\":60,\"unknown\":true}", "invalid_selector")]
    public async Task SecondsAndSelectorCounting_UseUpstreamCodes(string selector, string code)
    {
        using var fixture = new Fixture();
        await fixture.RejectCreateAndUpdate(selector, code);
    }

    [Theory]
    [InlineData("null", "invalid_rule")]
    [InlineData("\"60\"", "invalid_rule")]
    [InlineData("false", "invalid_rule")]
    [InlineData("0", "invalid_rule")]
    [InlineData("-1", "invalid_rule")]
    [InlineData("1.5", "invalid_rule")]
    [InlineData("9007199254740992", "invalid_rule")]
    [InlineData("9007199254740991", "time_out_of_range")]
    public async Task AfterSeconds_RequiresPositiveSafeInteger(string seconds, string code)
    {
        using var fixture = new Fixture();
        var args = JsonNode.Parse($$"""{"title":"check","prompt":"check","after_seconds":{{seconds}} }""")!.AsObject();
        var failure = Assert.IsType<ToolExecutionResult.Failure>(await fixture.Execute("schedule_create", args));
        Assert.Equal(code, failure.Error.Info?.Code);
    }

    [Theory]
    [InlineData("at", "09:00:00", "09:00:00.000")]
    [InlineData("at", "09:00:00.1", "09:00:00.100")]
    [InlineData("at", "09:00:00.12", "09:00:00.120")]
    [InlineData("at", "09:00:00.123", "09:00:00.123")]
    [InlineData("daily", "09:00:00", "09:00:00.000")]
    [InlineData("daily", "09:00:00.1", "09:00:00.100")]
    [InlineData("daily", "09:00:00.12", "09:00:00.120")]
    [InlineData("daily", "09:00:00.123", "09:00:00.123")]
    [InlineData("weekly", "09:00:00", "09:00:00.000")]
    [InlineData("weekly", "09:00:00.1", "09:00:00.100")]
    [InlineData("weekly", "09:00:00.12", "09:00:00.120")]
    [InlineData("weekly", "09:00:00.123", "09:00:00.123")]
    public async Task FractionalLocalTimes_SurviveCreateUpdateAndList(string kind, string time, string normalized)
    {
        using var fixture = new Fixture();
        var timing = new JsonObject { ["time"] = time, ["time_zone"] = "UTC" };
        if (kind == "at")
            timing["date"] = "2026-10-15";
        if (kind == "weekly")
            timing["weekdays"] = JsonNode.Parse("[4.0]");
        var result = await fixture.Success("schedule_create", new JsonObject
        {
            ["title"] = "check", ["prompt"] = "check", [kind] = timing.DeepClone(),
        });
        var id = result.GetProperty("id").GetString();
        Assert.Equal($"2026-10-15T{normalized}Z", result.GetProperty("scheduledAt").GetString());
        if (kind != "at")
            Assert.Equal(normalized, result.GetProperty("time").GetString());
        result = await fixture.Success("schedule_update", new JsonObject { ["id"] = id, [kind] = timing });
        Assert.Equal($"2026-10-15T{normalized}Z", result.GetProperty("scheduledAt").GetString());
        var list = await fixture.Success("schedule_list", new JsonObject());
        var stored = Assert.Single(list.EnumerateArray());
        Assert.Equal(result.GetRawText(), stored.GetRawText());
    }

    [Theory]
    [InlineData(" 2026-10-16")]
    [InlineData("2026-10-16 ")]
    [InlineData("2026-2-16")]
    [InlineData("2026-02-30")]
    public async Task LocalDates_AreStrictAndUntrimmed(string date)
    {
        using var fixture = new Fixture();
        await fixture.RejectCreateAndUpdate(new JsonObject
        {
            ["at"] = new JsonObject { ["date"] = date, ["time"] = "09:00:00", ["time_zone"] = "UTC" },
        }.ToJsonString(), "invalid_rule");
    }

    [Theory]
    [InlineData("0 9 16 * 1", "2026-10-16T09:00:00.000Z")]
    [InlineData("0 9 */2 * 1", "2026-10-19T09:00:00.000Z")]
    [InlineData("0 9 1-31 * 1", "2026-10-15T09:00:00.000Z")]
    [InlineData("0 9 * * 1", "2026-10-19T09:00:00.000Z")]
    [InlineData("0 9 1,* * 1", "2026-10-15T09:00:00.000Z")]
    [InlineData("0 9 *,1 * 1", "2026-10-19T09:00:00.000Z")]
    public async Task CronDaySelection_UsesVixieStarFlagsThroughRealTools(string expression, string expected)
    {
        using var fixture = new Fixture();
        var timing = new JsonObject { ["expression"] = expression, ["time_zone"] = "UTC" };
        var created = await fixture.Success("schedule_create", new JsonObject
        {
            ["title"] = "check", ["prompt"] = "check", ["cron"] = timing.DeepClone(),
        });
        Assert.Equal(expected, created.GetProperty("scheduledAt").GetString());
        var updated = await fixture.Success("schedule_update", new JsonObject
        {
            ["id"] = created.GetProperty("id").GetString(), ["cron"] = timing,
        });
        Assert.Equal(expected, updated.GetProperty("scheduledAt").GetString());
    }
}
