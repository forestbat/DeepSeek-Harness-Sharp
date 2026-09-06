using System.Text.Json;
using Dsh.Telemetry;

namespace Dsh.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void RecordsEventsAsJsonLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "telemetry-test", $"{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var telemetry = new TelemetryService(path))
            {
                telemetry.Record("agent/start", new { sessionId = "s1" });
                telemetry.Record("agent/end", new { sessionId = "s1", reason = "completed" });
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("agent/start", first.RootElement.GetProperty("event").GetString());
            Assert.Equal("s1", first.RootElement.GetProperty("payload").GetProperty("sessionId").GetString());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
