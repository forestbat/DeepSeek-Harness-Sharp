using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Telemetry;

public sealed class TelemetryService : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _disposed;

    public TelemetryService(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        _writer = new StreamWriter(outputPath, append: true) { AutoFlush = true };
    }

    public void Record(string eventName, object? payload = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["event"] = eventName,
            ["payload"] = payload,
        }, DshJson.Options);
        _writer.WriteLine(line);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _writer.Dispose();
    }
}
