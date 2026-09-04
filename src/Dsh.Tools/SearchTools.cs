using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Dsh.Tools;

internal static class SearchFiles
{
    internal static readonly IReadOnlySet<string> VcsDirectories = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git", ".svn", ".hg", ".bzr", ".jj", ".sl",
    };

    internal static List<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        if (open < 0) return [pattern];
        var depth = 0;
        var close = -1;
        for (var index = open; index < pattern.Length; index++)
        {
            if (pattern[index] == '{') depth++;
            else if (pattern[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    close = index;
                    break;
                }
            }
        }
        if (close < 0) return [pattern];
        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];
        var alternatives = new List<string>();
        var segmentDepth = 0;
        var segmentStart = open + 1;
        for (var index = open + 1; index <= close; index++)
        {
            var atEnd = index == close;
            if (!atEnd && pattern[index] == '{') segmentDepth++;
            else if (!atEnd && pattern[index] == '}') segmentDepth--;
            if (atEnd || (pattern[index] == ',' && segmentDepth == 0))
            {
                alternatives.Add(pattern[segmentStart..index]);
                segmentStart = index + 1;
            }
        }
        return alternatives.SelectMany(alternative => ExpandBraces(prefix + alternative + suffix)).ToList();
    }

    internal static Matcher CreateMatcher(string pattern)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        foreach (var expanded in ExpandBraces(pattern))
            matcher.AddInclude(expanded.Contains('/') ? expanded : $"**/{expanded}");
        return matcher;
    }

    internal static bool Matches(Matcher matcher, string relativePath)
        => matcher.Match(relativePath.Replace('\\', '/')).HasMatches;

    internal static IEnumerable<string> EnumerateFiles(string root, bool includeHidden, CancellationToken signal)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            signal.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (VcsDirectories.Contains(name)) continue;
                if (!includeHidden && name.StartsWith('.')) continue;
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null) continue;
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else if (File.Exists(entry))
                    yield return entry;
            }
        }
    }

    internal static string DisplayPath(string workdir, string absolutePath)
    {
        var relative = Path.GetRelativePath(workdir, absolutePath);
        if (relative == "..") return absolutePath;
        if (relative.StartsWith($"..{Path.DirectorySeparatorChar}")) return absolutePath;
        return relative.Replace('\\', '/');
    }

    internal static bool LooksBinary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer.AsSpan(0, read).IndexOf((byte)0) >= 0;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}

public sealed record GlobToolCaps
{
    public int MaxResults { get; init; } = 100;
    public long TimeoutMs { get; init; } = 30_000;
}

public sealed record GlobResultValue(string Root, IReadOnlyList<string> Paths);

public static class GlobTool
{
    public const string ToolName = "glob";

    private const string SectionText = "Use the glob tool — not shell find — to discover files by path pattern. A pattern with no \"/\" matches basenames at any depth, so \"*\" matches every file in the tree rather than its top level. "
        + "Results are files only, never directories, and include hidden and ignored files: a result that fits comes back in modification-time order, while a larger one keeps the modification-time-ordered head.";

    public static IDisposable Register(Context ctx, GlobToolCaps? caps = null)
    {
        var resolved = caps ?? new GlobToolCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:glob", PromptOrders.ToolGlob, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Find files whose paths match a glob pattern. Returns matching file paths — never directories — "
                + "including hidden and ignored files (VCS metadata directories are excluded). "
                + $"Up to {resolved.MaxResults} paths come back in modification-time order; a larger result returns the first {resolved.MaxResults} paths in modification-time order, "
                + "says so, and reports where the complete sorted list was saved. This tool does not enumerate directory entries.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["pattern"] = ToolSchemas.StringParam(
                        "Glob pattern to match file paths against (e.g. \"**/*.ts\", \"src/**/*.test.js\"). "
                        + "A pattern with no \"/\" matches the basename at any depth, so \"*\" and \"*.ts\" both search the whole tree; include a separator to anchor the depth."),
                    ["path"] = ToolSchemas.StringParam("Directory to search in. Defaults to the session workspace; a relative path resolves against it."),
                },
                "pattern"),
            TimeoutMs = resolved.TimeoutMs,
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["root", "paths"],
                      "properties": {
                        "root": { "type": "string" },
                        "paths": { "type": "array", "items": { "type": "string" } }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var result = value.Deserialize<GlobResultValue>(DshJson.Options)
                        ?? throw new JsonException("glob result value is malformed");
                    return [new TextBlock(RenderGlobPaths(result.Paths, resolved))];
                }),
            Execute = (args, exec) => Execute(args, exec),
        });
        return new CompositeDisposable(section, registration);
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec)
    {
        var pattern = args.GetProperty("pattern").GetString() ?? "";
        if (pattern.Trim().Length == 0)
            throw new ArgumentException("pattern must be a non-empty string");
        var pathArg = args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        if (pathArg is not null && pathArg.Trim().Length == 0)
            throw new ArgumentException("path must be a non-empty string when given");
        var sessionCwd = exec.Agent?.Session.Header.Cwd;
        var workdir = sessionCwd ?? Environment.CurrentDirectory;
        var root = pathArg is null
            ? workdir
            : Path.IsPathRooted(pathArg)
                ? Path.GetFullPath(pathArg)
                : Path.GetFullPath(Path.Combine(workdir, pathArg));
        if (!Directory.Exists(root))
            throw new HarnessException($"glob search failed (exit 2): the directory does not exist: \"{root}\"", "SEARCH_FAILED");
        var matcher = SearchFiles.CreateMatcher(pattern);
        var matches = new List<string>();
        foreach (var file in SearchFiles.EnumerateFiles(root, includeHidden: true, exec.Signal))
        {
            if (SearchFiles.Matches(matcher, Path.GetRelativePath(root, file)))
                matches.Add(file);
        }
        var ordered = matches
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path), Comparer<DateTime>.Default)
            .Select(path => SearchFiles.DisplayPath(workdir, path))
            .ToList();
        var displayRoot = pathArg is null ? "." : SearchFiles.DisplayPath(workdir, root);
        return Task.FromResult<object?>(new GlobResultValue(displayRoot, ordered));
    }

    internal static string RenderGlobPaths(IReadOnlyList<string> paths, GlobToolCaps caps)
    {
        if (paths.Count == 0) return "No files found";
        if (paths.Count <= caps.MaxResults) return string.Join('\n', paths);
        return FormatGlobPage(paths.Take(caps.MaxResults).ToList(), paths.Count);
    }

    private static string FormatGlobPage(IReadOnlyList<string> items, int seen)
        => $"{string.Join('\n', items)}\n\n(Showing {items.Count} of {seen} paths. The complete result could not be saved; narrow pattern or path to see more.)";
}

public sealed record GrepToolCaps
{
    public int MaxMatches { get; init; } = 250;
    public int MaxLineBytes { get; init; } = 2000;
    public long TimeoutMs { get; init; } = 30_000;
}

public sealed record GrepMatchValue(string Path, int LineNumber, string Line);

public sealed record GrepResultValue(IReadOnlyList<GrepMatchValue> Matches);

public static class GrepTool
{
    public const string ToolName = "grep";

    private const string SectionText = "Use the grep tool — not shell grep or rg — to search file contents. Use read on a matched file when you need surrounding context.";

    public static IDisposable Register(Context ctx, GrepToolCaps? caps = null)
    {
        var resolved = caps ?? new GrepToolCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:grep", PromptOrders.ToolGrep, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Search file contents with a ripgrep regular expression. Returns matching lines with line numbers, grouped by file. "
                + $"Returns the first {resolved.MaxMatches} matches inline; a capped result reports where the complete match list was saved. "
                + "Use read on a matched file for surrounding context.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["pattern"] = ToolSchemas.StringParam("Regular expression to search for (ripgrep syntax)."),
                    ["path"] = ToolSchemas.StringParam("File or directory to search. Defaults to the session workspace; a relative path resolves against it."),
                    ["include"] = ToolSchemas.StringParam("One glob filter for which files to search (e.g. \"*.ts\", \"*.{js,jsx}\"). Not a list; negation is not supported."),
                },
                "pattern"),
            TimeoutMs = resolved.TimeoutMs,
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["matches"],
                      "properties": {
                        "matches": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["path", "lineNumber", "line"],
                            "properties": {
                              "path": { "type": "string" },
                              "lineNumber": { "type": "integer" },
                              "line": { "type": "string" }
                            }
                          }
                        }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var result = value.Deserialize<GrepResultValue>(DshJson.Options)
                        ?? throw new JsonException("grep result value is malformed");
                    return [new TextBlock(FormatRetainedGrep(RetainGrepMatches(result.Matches, resolved.MaxMatches, resolved.MaxLineBytes)))];
                }),
            Execute = (args, exec) => Execute(args, exec),
        });
        return new CompositeDisposable(section, registration);
    }

    private static void ValidateInclude(string include)
    {
        if (include.Trim().Length == 0)
            throw new ArgumentException("include must be a non-empty glob when given");
        if (include.StartsWith('!'))
            throw new ArgumentException("include must be a positive glob filter; negated patterns (\"!…\") are not supported");
        var braceDepth = 0;
        foreach (var character in include)
        {
            if (character == '{') braceDepth++;
            else if (character == '}') braceDepth = Math.Max(0, braceDepth - 1);
            else if (character == ',' && braceDepth == 0)
                throw new ArgumentException("include must be one glob, not a comma-separated list (use {a,b} alternation instead)");
        }
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec)
    {
        var pattern = args.GetProperty("pattern").GetString() ?? "";
        if (pattern.Length == 0)
            throw new ArgumentException("pattern must be a non-empty string");
        var pathArg = args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        if (pathArg is not null && pathArg.Trim().Length == 0)
            throw new ArgumentException("path must be a non-empty string when given");
        var include = args.TryGetProperty("include", out var includeElement) && includeElement.ValueKind == JsonValueKind.String
            ? includeElement.GetString()
            : null;
        if (include is not null)
            ValidateInclude(include);
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException error)
        {
            throw new HarnessException($"grep pattern rejected: {error.Message}", "SEARCH_INVALID_PATTERN", error);
        }
        var sessionCwd = exec.Agent?.Session.Header.Cwd;
        var workdir = sessionCwd ?? Environment.CurrentDirectory;
        var root = pathArg is null
            ? workdir
            : Path.IsPathRooted(pathArg)
                ? Path.GetFullPath(pathArg)
                : Path.GetFullPath(Path.Combine(workdir, pathArg));
        var includeMatcher = include is null ? null : SearchFiles.CreateMatcher(include);
        var matches = new List<GrepMatchValue>();
        IEnumerable<string> files;
        if (File.Exists(root))
        {
            files = [root];
        }
        else if (Directory.Exists(root))
        {
            files = SearchFiles.EnumerateFiles(root, includeHidden: false, exec.Signal).OrderBy(path => path, StringComparer.Ordinal);
        }
        else
        {
            throw new HarnessException($"grep search failed (exit 2): the path does not exist: \"{root}\"", "SEARCH_FAILED");
        }
        foreach (var file in files)
        {
            exec.Signal.ThrowIfCancellationRequested();
            if (includeMatcher is not null && !SearchFiles.Matches(includeMatcher, Path.GetRelativePath(root, file)))
                continue;
            if (SearchFiles.LooksBinary(file))
                continue;
            ScanFile(file, regex, matches, workdir, exec.Signal);
        }
        return Task.FromResult<object?>(new GrepResultValue(matches));
    }

    private static void ScanFile(string file, Regex regex, List<GrepMatchValue> matches, string workdir, CancellationToken signal)
    {
        using var reader = new StreamReader(file, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber += 1;
            if ((lineNumber & 0x3FF) == 0)
                signal.ThrowIfCancellationRequested();
            if (regex.IsMatch(line))
                matches.Add(new GrepMatchValue(SearchFiles.DisplayPath(workdir, file), lineNumber, line));
        }
    }

    internal static string PreviewLine(string line, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(line) <= maxBytes)
            return line;
        var budget = 0;
        var cut = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (budget + runeBytes > maxBytes)
                break;
            budget += runeBytes;
            cut += rune.Utf16SequenceLength;
        }
        return $"{line[..cut]} (line truncated)";
    }

    private sealed record RetainedMatches(IReadOnlyList<GrepMatchValue> Items, int Kept, int Seen)
    {
        public bool Truncated => Kept < Seen;
    }

    private static RetainedMatches RetainGrepMatches(IReadOnlyList<GrepMatchValue> matches, int maxMatches, int maxLineBytes)
    {
        var kept = matches.Take(maxMatches)
            .Select(match => match with { Line = PreviewLine(match.Line, maxLineBytes) })
            .ToList();
        return new RetainedMatches(kept, kept.Count, matches.Count);
    }

    private static string MatchNoun(int count) => count == 1 ? "match" : "matches";

    internal static string FormatGrepMatches(IReadOnlyList<GrepMatchValue> matches)
    {
        var byFile = new Dictionary<string, List<GrepMatchValue>>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (!byFile.TryGetValue(match.Path, out var group))
                byFile[match.Path] = group = [];
            group.Add(match);
        }
        return string.Join("\n\n", byFile.Select(pair => $"{pair.Key}\n{string.Join('\n', pair.Value.Select(m => $"Line {m.LineNumber}: {m.Line}"))}"));
    }

    private static string FormatGrepOutput(RetainedMatches retained)
    {
        var header = retained.Truncated
            ? $"Found {retained.Kept} of {retained.Seen} matches"
            : $"Found {retained.Seen} {MatchNoun(retained.Seen)}";
        var body = FormatGrepMatches(retained.Items);
        if (!retained.Truncated)
            return $"{header}\n\n{body}";
        return $"{header}\n\n{body}\n\n(The complete result could not be saved; narrow pattern, path, or include to see more.)";
    }

    private static string FormatRetainedGrep(RetainedMatches retained)
    {
        if (retained.Seen == 0) return "No matches found";
        return FormatGrepOutput(retained);
    }
}
