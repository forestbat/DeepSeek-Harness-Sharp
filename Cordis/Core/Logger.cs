using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cordis;

public enum LoggerType
{
    Error,
    Warn,
    Info,
    Debug,
}

public static class LoggerLevel
{
    public const int Error = 0;
    public const int Warn = 1;
    public const int Info = 2;
    public const int Debug = 3;
}

public sealed record Message
{
    public long Sn { get; init; }
    public long Ts { get; init; }
    public required string Name { get; init; }
    public LoggerType Type { get; init; }
    public int Level { get; init; }
    public required object?[] Args { get; init; }
    public WeakReference<Fiber>? Fiber { get; init; }
}

public delegate string Formatter(object? value, Exporter exporter, Message message);

public interface Exporter
{
    int Colors { get; }
    int? MaxLength { get; }
    IReadOnlyDictionary<string, int>? Levels => null;
    IReadOnlyDictionary<char, Formatter>? Formatters => null;
    void Export(Message message);
}

public static class DefaultFormatters
{
    public static string FormatString(object? value, Exporter exporter, Message message) => value?.ToString() ?? "null";
    public static string FormatInt(object? value, Exporter exporter, Message message) => Convert.ToInt64(value).ToString();
    public static string FormatFloat(object? value, Exporter exporter, Message message) => Convert.ToDouble(value).ToString();
    public static string FormatJson(object? value, Exporter exporter, Message message) => JsonSerializer.Serialize(value);
    public static string FormatEmpty(object? value, Exporter exporter, Message message) => "";
    public static string FormatColored(object? value, Exporter exporter, Message message) =>
        Logger.Color(exporter, Logger.Code(message.Name, exporter.Colors), value);

    public static IReadOnlyDictionary<char, Formatter> Map { get; } = new Dictionary<char, Formatter>
    {
        ['s'] = FormatString,
        ['d'] = FormatInt,
        ['i'] = FormatInt,
        ['f'] = FormatFloat,
        ['o'] = FormatJson,
        ['O'] = FormatJson,
        ['c'] = FormatEmpty,
        ['C'] = FormatColored,
    };
}

public sealed class Logger
{
    public static readonly int[] C16 = [6, 2, 3, 4, 5, 1];
    public static readonly int[] C256 =
    [
        20, 21, 26, 27, 32, 33, 38, 39, 40, 41, 42, 43, 44, 45, 56, 57, 62,
        63, 68, 69, 74, 75, 76, 77, 78, 79, 80, 81, 92, 93, 98, 99, 112, 113,
        129, 134, 135, 148, 149, 160, 161, 162, 163, 164, 165, 166, 167, 168,
        169, 170, 171, 172, 173, 178, 179, 184, 185, 196, 197, 198, 199, 200,
        201, 202, 203, 204, 205, 206, 207, 208, 209, 214, 215, 220, 221,
    ];

    public const int DefaultMaxLength = 10240;

    private static readonly Regex Placeholder = new("%([a-zA-Z%])", RegexOptions.Compiled);

    public string Name { get; }
    public int? Level { get; }
    public Message? Meta { get; }
    private readonly LoggerService _service;

    internal Logger(string name, int? level, Message? meta, LoggerService service)
    {
        Name = name;
        Level = level;
        Meta = meta;
        _service = service;
    }

    public static string Color(Exporter exporter, int code, object? value, string decoration = "")
    {
        if (exporter.Colors <= 0) return $"{value}";
        var color = code < 8 ? $"3{code}" : $"38;5;{code}";
        var decorationPart = exporter.Colors >= 2 ? decoration : "";
        return $"[{color}{decorationPart}m{value}[0m";
    }

    public static int Code(string name, int colors)
    {
        var hash = 0;
        foreach (var c in name)
        {
            hash = ((hash << 3) - hash) + c + 13;
            hash |= 0;
        }
        var palette = colors == 0 ? [] : colors >= 2 ? C256 : C16;
        return palette.Length == 0 ? 0 : palette[Math.Abs(hash) % palette.Length];
    }

    public static string Format(Exporter exporter, Message message)
    {
        var args = message.Args.ToList();
        if (args.Count > 0 && args[0] is Exception error)
        {
            args[0] = error.ToString();
            args.Insert(0, "%s");
        }
        else if (args.Count == 0 || args[0] is not string)
        {
            args.Insert(0, "%o");
        }

        var format = (string)args[0]!;
        args.RemoveAt(0);
        format = Placeholder.Replace(format, match =>
        {
            if (match.Value == "%%") return "%";
            var c = match.Groups[1].Value[0];
            var formatter = exporter.Formatters?.GetValueOrDefault(c) ?? DefaultFormatters.Map.GetValueOrDefault(c);
            if (formatter is null) return match.Value;
            var value = args.Count > 0 ? args[0] : null;
            if (args.Count > 0) args.RemoveAt(0);
            return formatter(value, exporter, message);
        });

        var jsonFormatter = exporter.Formatters?.GetValueOrDefault('o') ?? DefaultFormatters.FormatJson;
        foreach (var arg in args)
        {
            var text = arg is string or null ? arg?.ToString() : jsonFormatter(arg, exporter, message);
            format += $" {text}";
        }

        var maxLength = exporter.MaxLength ?? DefaultMaxLength;
        return string.Join('\n', format.Split('\n').Select(line =>
            line.Length > maxLength ? $"{line[..maxLength]}..." : line));
    }

    public void Error(object? format, params object?[] args) => Log(LoggerType.Error, LoggerLevel.Error, format, args);
    public void Warn(object? format, params object?[] args) => Log(LoggerType.Warn, LoggerLevel.Warn, format, args);
    public void Info(object? format, params object?[] args) => Log(LoggerType.Info, LoggerLevel.Info, format, args);
    public void Debug(object? format, params object?[] args) => Log(LoggerType.Debug, LoggerLevel.Debug, format, args);

    private void Log(LoggerType type, int level, object? format, object?[] args)
    {
        var allArgs = format is null ? args : [format, .. args];
        if (allArgs.Length == 1 && allArgs[0] is Exception single)
        {
            if (single is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) Log(type, level, inner, []);
                return;
            }
        }
        var sn = _service.NextMessageSn();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var exporter in _service.Exporters.Values)
        {
            int? targetLevel = null;
            if (exporter.Levels is not null)
            {
                if (exporter.Levels.TryGetValue(Name, out var named)) targetLevel = named;
                else if (exporter.Levels.TryGetValue("default", out var fallback)) targetLevel = fallback;
            }
            targetLevel ??= Level ?? LoggerLevel.Info;
            if (targetLevel < level) continue;
            var message = new Message
            {
                Sn = sn,
                Ts = ts,
                Type = type,
                Level = level,
                Name = Name,
                Args = allArgs,
                Fiber = Meta?.Fiber,
            };
            exporter.Export(message);
        }
    }
}

public sealed class LoggerService
{
    private sealed class State
    {
        public long SnMessage;
        public long SnExporter;
        public readonly Dictionary<long, Exporter> Exporters = new();
        public readonly List<Message> Buffer = new();
    }

    private readonly State _state;

    public Context Ctx { get; }
    public int BufferSize { get; set; } = 1000;

    public IReadOnlyDictionary<long, Exporter> Exporters => _state.Exporters;
    public IReadOnlyList<Message> Buffer => _state.Buffer;

    public LoggerService(Context ctx)
    {
        Ctx = ctx;
        _state = new State();
        Exporter(new BufferExporter(this));
    }

    private LoggerService(Context ctx, State state)
    {
        Ctx = ctx;
        _state = state;
    }

    public LoggerService Bind(Context ctx) => new(ctx, _state);

    internal long NextMessageSn() => ++_state.SnMessage;

    public EffectHandle Exporter(Exporter exporter)
    {
        return Ctx.Effect(() =>
        {
            var id = ++_state.SnExporter;
            _state.Exporters[id] = exporter;
            return () => _state.Exporters.Remove(id);
        }, "ctx.logger.exporter()");
    }

    public Logger Invoke(string? name = null, Fiber? caller = null)
    {
        var config = ResolveIntercept();
        var fiber = caller ?? Ctx.Fiber;
        name ??= config?.Name;
        name ??= CordisUtils.Hyphenate(fiber.Name);
        return new Logger(name, config?.Level, new Message
        {
            Sn = 0,
            Ts = 0,
            Name = name,
            Args = [],
            Fiber = new WeakReference<Fiber>(fiber),
        }, this);
    }

    private LoggerIntercept? ResolveIntercept()
    {
        LoggerIntercept? result = null;
        foreach (var level in Ctx.InterceptMap.Chain())
        {
            if (!level.HasOwn("logger")) continue;
            var value = level["logger"];
            if (value is LoggerIntercept intercept)
            {
                result = new LoggerIntercept
                {
                    Name = intercept.Name ?? result?.Name,
                    Level = intercept.Level ?? result?.Level,
                };
            }
        }
        return result;
    }

    public void Error(object? format, params object?[] args) => Invoke().Error(format, args);
    public void Warn(object? format, params object?[] args) => Invoke().Warn(format, args);
    public void Info(object? format, params object?[] args) => Invoke().Info(format, args);
    public void Debug(object? format, params object?[] args) => Invoke().Debug(format, args);

    public sealed record LoggerIntercept
    {
        public string? Name { get; init; }
        public int? Level { get; init; }
    }

    private sealed class BufferExporter(LoggerService service) : Exporter
    {
        public int Colors => 3;
        public int? MaxLength => null;

        public void Export(Message message)
        {
            service._state.Buffer.Add(message);
            var overflow = service._state.Buffer.Count - service.BufferSize;
            if (overflow > 0) service._state.Buffer.RemoveRange(0, overflow);
        }
    }
}
