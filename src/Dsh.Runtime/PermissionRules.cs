using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Dsh.Runtime;

/** Stable IDs identify rules; dimensions within one matcher are conjunctive. */
public class PermissionRules
{
    [YamlMember(Alias = "autoApprove")]
    public bool AutoApprove { get; set; }
    [YamlMember(Alias = "blacklist")]
    public Dictionary<string, PermissionMatcher> Blacklist { get; set; } = [];
    [YamlMember(Alias = "whitelist")]
    public Dictionary<string, PermissionMatcher> Whitelist { get; set; } = [];

    public void Validate()
    {
        foreach (var (id, matcher) in Blacklist.Concat(Whitelist))
        {
            if (string.IsNullOrWhiteSpace(id) || matcher is null)
                throw new ArgumentException("Permission rules require nonempty IDs and matcher mappings.");
            matcher.Validate();
        }
    }
}

public sealed class PermissionMatcher
{
    /** One tool name or a nonempty sequence; omitted means every tool. */
    [YamlMember(Alias = "tool")]
    public IReadOnlyList<string>? Tool { get; set; }
    [YamlMember(Alias = "dialect")]
    public string? Dialect { get; set; }
    [YamlMember(Alias = "path")]
    public string? Path { get; set; }
    [YamlMember(Alias = "intent")]
    public string Intent { get; set; } = "any";
    [YamlMember(Alias = "args")]
    public Dictionary<string, string> Args { get; set; } = [];

    public void Validate()
    {
        if (Tool is { Count: 0 } || Tool?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("tool must be a name or a nonempty list of names.");
        if (Dialect is not (null or "posix" or "powershell"))
            throw new ArgumentException("dialect must be posix or powershell.");
        if (Intent is not ("any" or "read" or "write"))
            throw new ArgumentException("intent must be read, write or any.");
        if (Path is not null && string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("path must be a nonempty glob.");
        if (Args.ContainsKey("path") || Args.ContainsKey("file_path"))
            throw new ArgumentException("Path arguments use the unified path matcher, not args.path or args.file_path.");
        if (Args.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("args must map parameter names to string patterns.");
    }
}

/** Explicit scalar-or-sequence conversion, including static YAML and NativeAOT. */
public sealed class RuleToolsYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(IReadOnlyList<string>);
    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var values = new List<string>();
        if (parser.TryConsume<Scalar>(out var scalar))
            values.Add(scalar.Value);
        else
        {
            parser.Consume<SequenceStart>();
            while (!parser.TryConsume<SequenceEnd>(out _))
                values.Add(parser.Consume<Scalar>().Value);
        }
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new YamlException("tool must contain at least one nonempty tool name.");
        return values;
    }
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var tools = (IReadOnlyList<string>)value!;
        if (tools.Count == 1)
            emitter.Emit(new Scalar(tools[0]));
        else
        {
            emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
            foreach (var tool in tools)
                emitter.Emit(new Scalar(tool));
            emitter.Emit(new SequenceEnd());
        }
    }
}

public sealed record PermissionInput(string? Tool, JsonElement Arguments, string WorkingDirectory,
    string? Path = null, string? Intent = null, ShellDialect? Dialect = null,
    Func<string, bool>? IsFileAlias = null,
    Func<int, string, string, (bool LiteralMatch, bool CaseSensitive)>? ComparePathComponent = null,
    bool PathIsCanonical = false);
public readonly record struct PermissionField(string Field, string Pattern);
public sealed record PermissionMatch(string RuleId, string RuleKind, IReadOnlyList<PermissionField> Fields);
public readonly record struct PermissionDecision(string Decision, bool AutoApprove, PermissionMatch? Match);

public static class PermissionEngine
{
    public static ShellDialect? DialectOf(string? tool)
        => tool is not null && ShellToolDialects.ByToolName.TryGetValue(tool, out var dialect) ? dialect : null;

    public static PermissionDecision Decide(PermissionRules policy, PermissionInput input,
        Func<string, string> physicalPath)
    {
        var deny = Match(policy.Blacklist, "blacklist", input, physicalPath);
        if (deny is not null)
            return new("deny", policy.AutoApprove, deny);
        if (policy.AutoApprove) return new("allow", true, null);
        var allow = Match(policy.Whitelist, "whitelist", input, physicalPath);
        return new(allow is not null ? "allow" : "notify", false, allow);
    }

    public static PermissionMatch? Match(IReadOnlyDictionary<string, PermissionMatcher> rules, string kind,
        PermissionInput input, Func<string, string> physicalPath)
    {
        foreach (var (id, matcher) in rules)
        {
            var fields = MatchFields(matcher, input, physicalPath);
            if (fields is not null)
                return new(id, kind, fields);
        }
        return null;
    }

    public static IReadOnlyList<PermissionField>? MatchFields(PermissionMatcher matcher, PermissionInput input,
        Func<string, string> physicalPath, bool omitFileDimensions = false)
    {
        var dialect = input.Dialect ?? DialectOf(input.Tool);
        if (matcher.Tool is { } tools)
        {
            if (input.Tool is null || !tools.Contains(input.Tool, StringComparer.Ordinal)) return null;
        }
        if (matcher.Dialect is { } expected)
        {
            if (dialect is null || expected != DialectName(dialect)) return null;
        }
        foreach (var (name, pattern) in matcher.Args)
        {
            if (input.Arguments.ValueKind != JsonValueKind.Object
                || !input.Arguments.TryGetProperty(name, out var argument)
                || argument.ValueKind != JsonValueKind.String) return null;
            var value = argument.GetString()!;
            var matches = name == "command" && dialect is { } shell
                ? ShellCommandPattern.Parse(pattern, shell).Matches(ShellCommandSegments.Split(value, shell))
                : StringGlob(pattern, value);
            if (!matches) return null;
        }
        if (!omitFileDimensions)
        {
            if (matcher.Intent != "any")
            {
                if (input.Intent != matcher.Intent) return null;
            }
            if (matcher.Path is { } pattern)
            {
                if (input.Path is null) return null;
                var normalized = NormalizePattern(pattern, input.WorkingDirectory, physicalPath);
                var actualPath = PathText(input.PathIsCanonical ? input.Path : physicalPath(input.Path));
                if (!PathGlob(normalized, actualPath, input.ComparePathComponent)
                    && !(normalized.IndexOfAny(['*', '?']) < 0 && input.IsFileAlias?.Invoke(normalized) == true)) return null;
            }
        }
        var fields = new List<PermissionField>(matcher.Args.Count + 4);
        if (matcher.Tool is not null) fields.Add(new("tool", input.Tool!));
        if (matcher.Dialect is not null) fields.Add(new("dialect", matcher.Dialect));
        foreach (var (name, pattern) in matcher.Args) fields.Add(new($"args.{name}", pattern));
        if (!omitFileDimensions && matcher.Intent != "any") fields.Add(new("intent", matcher.Intent));
        if (!omitFileDimensions && matcher.Path is not null) fields.Add(new("path", matcher.Path));
        return fields;
    }

    public static string? DialectName(ShellDialect? dialect)
        => dialect switch { ShellDialect.Posix => "posix", ShellDialect.PowerShell => "powershell", _ => null };

    /** Canonicalize the literal prefix, never ask the filesystem to resolve wildcard components. */
    public static string NormalizePattern(string pattern, string cwd, Func<string, string> physicalPath)
    {
        var absolute = PathText(System.IO.Path.GetFullPath(pattern, cwd));
        var wildcard = absolute.IndexOfAny(['*', '?']);
        if (wildcard < 0) return PathText(physicalPath(absolute));
        var separator = absolute.LastIndexOf('/', wildcard);
        var root = PathText(System.IO.Path.GetPathRoot(absolute)!);
        var prefix = separator < root.Length ? root : absolute[..separator];
        return PathText(physicalPath(prefix)).TrimEnd('/') + absolute[separator..];
    }

    public static string PathText(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static bool PathGlob(string pattern, string value,
        Func<int, string, string, (bool LiteralMatch, bool CaseSensitive)>? compare)
    {
        var patternRoot = PathText(System.IO.Path.GetPathRoot(pattern)!);
        var valueRoot = PathText(System.IO.Path.GetPathRoot(value)!);
        if (patternRoot != valueRoot && compare?.Invoke(-1, patternRoot, valueRoot).LiteralMatch != true) return false;
        var patterns = pattern[patternRoot.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var values = value[valueRoot.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rule = 0;
        var item = 0;
        var recursive = -1;
        var retry = 0;
        while (item < values.Length)
        {
            if (rule < patterns.Length && patterns[rule] == "**")
            {
                recursive = rule++;
                retry = item;
                continue;
            }
            if (rule < patterns.Length && Component(patterns[rule], values[item], item, compare))
            {
                rule++;
                item++;
                continue;
            }
            if (recursive < 0) return false;
            rule = recursive + 1;
            item = ++retry;
        }
        while (rule < patterns.Length && patterns[rule] == "**") rule++;
        return rule == patterns.Length;
    }

    private static bool Component(string pattern, string value, int index,
        Func<int, string, string, (bool LiteralMatch, bool CaseSensitive)>? compare)
    {
        if (pattern == value) return true;
        if (pattern.IndexOfAny(['*', '?']) < 0) return compare?.Invoke(index, pattern, value).LiteralMatch == true;
        // An exact-spelling wildcard match needs no filesystem case-folding assumption.
        if (StringGlob(pattern, value)) return true;
        if (compare is null) return false;
        var semantics = compare(index, pattern, value);
        if (pattern.Any(static character => character > 127) || value.Any(static character => character > 127))
            throw new NotSupportedException("This path wildcard requires non-ASCII filesystem equivalence that the pinned backend cannot express. Use literal components or an unrestricted * or ** segment.");
        return !semantics.CaseSensitive && StringGlob(pattern, value, ignoreCase: true);
    }

    private static bool StringGlob(string pattern, string value, bool ignoreCase = false)
    {
        var expression = new System.Text.StringBuilder(pattern.Length + 4).Append("\\A");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*') expression.Append(".*");
            else if (character == '?') expression.Append('.');
            else
            {
                if (".\\+()[]{}^$|".Contains(character)) expression.Append('\\');
                expression.Append(character);
            }
        }
        expression.Append("\\z");
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(value, expression.ToString(), options, TimeSpan.FromSeconds(1));
    }
}
