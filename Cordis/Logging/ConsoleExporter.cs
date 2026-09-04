using System.Text.Json;

namespace Cordis.Logging;

public sealed class LabelStyle
{
    public int? Width { get; init; }
    public int? Margin { get; init; }
    public string? Align { get; init; }
}

public class ConsoleExporter : Exporter
{
    public static readonly string DefaultTimeFormat = "yyyy-MM-dd HH:mm:ss ";

    public int Colors { get; init; }
    public int? MaxLength { get; init; }
    public IReadOnlyDictionary<string, int>? Levels { get; init; }
    public IReadOnlyDictionary<char, Formatter>? Formatters { get; protected set; }
    public bool ShowDiff { get; init; }
    public string ShowTime { get; init; } = DefaultTimeFormat;
    public LabelStyle? Label { get; init; }

    private long _timestamp;

    public ConsoleExporter(Context? ctx = null)
    {
        _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ctx?.Logger.Exporter(this);
    }

    public virtual void Export(Message message)
    {
        Console.WriteLine(Render(message));
    }

    public string Render(Message message)
    {
        var prefix = $"[{char.ToUpperInvariant(message.Type.ToString()[0])}]";
        var space = new string(' ', Label?.Margin ?? 1);
        var indent = 3 + space.Length;
        var output = "";
        if (ShowTime.Length > 0)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(message.Ts).LocalDateTime;
            indent += ShowTime.Length;
            output += Logger.Color(this, 8, time.ToString(ShowTime));
        }
        var code = Logger.Code(message.Name, Colors);
        var label = Logger.Color(this, code, message.Name, ";1");
        var padLength = (Label?.Width ?? 0) + VisibleLength(label, message.Name);
        if (Label?.Align == "right")
        {
            output += label.PadLeft(padLength) + space + prefix + space;
            indent += Label.Width ?? 0 + space.Length;
        }
        else
        {
            output += prefix + space + label.PadRight(padLength) + space;
        }
        output += Logger.Format(this, message).Replace("\n", "\n" + new string(' ', indent));
        if (ShowDiff && _timestamp > 0)
        {
            var diff = message.Ts - _timestamp;
            output += Logger.Color(this, code, $" +{FormatDiff(diff)}");
        }
        _timestamp = message.Ts;
        return output;
    }

    private static int VisibleLength(string label, string name)
    {
        return label.Length - name.Length;
    }

    private static string FormatDiff(long ms)
    {
        return ms switch
        {
            < 1000 => $"{ms}ms",
            < 60_000 => $"{ms / 1000.0:0.#}s",
            _ => $"{ms / 60_000.0:0.#}m",
        };
    }
}

public sealed class InspectConsoleExporter : ConsoleExporter
{
    public InspectConsoleExporter(Context? ctx = null) : base(ctx)
    {
        Formatters = new Dictionary<char, Formatter>
        {
            ['o'] = Inspect,
            ['O'] = Inspect,
        };
    }

    private static string Inspect(object? value, Exporter exporter, Message message)
    {
        return JsonSerializer.Serialize(value);
    }
}
