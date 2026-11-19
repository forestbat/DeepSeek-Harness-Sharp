using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsh.Runtime;
using Dsh.Llm;
using Microsoft.Extensions.FileSystemGlobbing;
using Dsh.Agent;
using Dsh.Safety;

namespace Dsh.Tools;

internal static class SearchFiles
{
    internal static readonly IReadOnlySet<string> VcsDirectories = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git", ".svn", ".hg", ".bzr", ".jj", ".sl",
    };

    /** 这些目录里没有源码, 只有构建产物与依赖缓存; 逐文件读一遍就让一次搜索变成整盘扫描。 */
    internal static readonly IReadOnlySet<string> GeneratedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "TestResults",
    };

    /** artifacts/bin 一类的产物目录常带日期后缀(artifacts020、artifacts-old), 按前缀认。 */
    private const string ArtifactsPrefix = "artifacts";

    internal static bool IsGeneratedDirectory(string name)
        => GeneratedDirectoryNames.Contains(name)
           || name.StartsWith(ArtifactsPrefix, StringComparison.OrdinalIgnoreCase);

    /** 这些扩展名不可能包含可读文本, 不必打开就能跳过: 省掉每文件一次 8 KB 预读。 */
    internal static readonly IReadOnlySet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".pdf",
        ".zip", ".gz", ".zst", ".zstd", ".7z", ".rar", ".tar", ".nupkg", ".snupkg", ".whl",
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".a", ".lib", ".obj", ".class", ".jar", ".wasm",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".mov", ".wav", ".mkv", ".webm", ".ogg", ".flac", ".avi",
        ".dmp", ".bin", ".dat", ".db", ".sqlite", ".sqlite3", ".parquet", ".arrow", ".onnx", ".pt", ".safetensors",
    };

    internal static bool LooksBinaryExtension(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

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

    internal static async IAsyncEnumerable<string> EnumerateFiles(string root, bool includeHidden, ToolRunContext exec,
        DirectoryPruner? pruner = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            exec.Signal.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var entries = await SafetyFileAccess.ReadDirectoryAsync(directory, exec).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry.Path);
                if (VcsDirectories.Contains(name)) continue;
                if (!includeHidden && name.StartsWith('.')) continue;
                if (entry.IsDirectory && pruner?.ShouldPrune(name) == true) continue;
                if (entry.IsDirectory) pending.Push(entry.Path);
                else yield return entry.Path;
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

    internal static async Task<bool> LooksBinary(string path, ToolRunContext exec)
    {
        await using var file = await SafetyFileAccess.OpenReadAsync(path, exec).ConfigureAwait(false);
        var buffer = new byte[8192];
        var read = await file.ReadAsync(buffer, exec.Signal).ConfigureAwait(false);
        return buffer.AsSpan(0, read).IndexOf((byte)0) >= 0;
    }

    internal static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return long.MaxValue;
        }
        catch (UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }
}

/**
 * 目录剪枝: 整棵跳过命中 predicate 的目录, 并记下跳过了几个, 好让结果里说明这次搜索没看全。
 * 只在用户没有点名目录时启用: 点名要搜 artifacts 里的日志是合法需求。
 */
internal sealed class DirectoryPruner(Func<string, bool> prune)
{
    private int _pruned;

    public int PrunedCount => Volatile.Read(ref _pruned);

    public bool ShouldPrune(string directoryName)
    {
        if (!prune(directoryName))
            return false;
        Interlocked.Increment(ref _pruned);
        return true;
    }

    public static DirectoryPruner BuildOutputs() => new(SearchFiles.IsGeneratedDirectory);
}

public sealed record GlobToolCaps
{
    public int MaxResults { get; init; } = 100;
    public long TimeoutMs { get; init; } = 30_000;
    /** 最多枚举这么多个文件就收场: 命名形状上百 GB 的树不该让一次 glob 变成整盘扫描。 */
    public int MaxScannedFiles { get; init; } = 50_000;
}

public sealed record GlobResultValue(string Root, IReadOnlyList<string> Paths, int ScannedFiles = 0, bool StoppedEarly = false);

public static class GlobTool
{
    public const string ToolName = "glob";

    private const string SectionText = "Use the glob tool — not shell find — to discover files by path pattern.";

    public static IDisposable Register(Context ctx, GlobToolCaps? caps = null)
    {
        var resolved = caps ?? new GlobToolCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:glob", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            MediatesFileAccess = true,
            Description = "Find files whose paths match a glob pattern. Returns matching file paths — never directories — "
                + "including hidden and ignored files (VCS metadata directories are excluded). "
                + $"Up to {resolved.MaxResults} paths come back in modification-time order; a larger result returns the first {resolved.MaxResults} paths in modification-time order, "
                + $"search stops after scanning {resolved.MaxScannedFiles} files and says so. This tool does not enumerate directory entries.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["pattern"] = ToolSchemas.StringParam(
                        "Glob pattern to match file paths against (e.g. \"**/*.ts\", \"src/**/*.test.js\"). "
                        + "A pattern with no \"/\" matches the basename at any depth, so \"*\" and \"*.ts\" both search the whole tree; include a separator to anchor the depth."),
                    ["path"] = ToolSchemas.StringParam("Directory to search in. Defaults to the session working directory; a relative path resolves against it."),
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
                        "paths": { "type": "array", "items": { "type": "string" } },
                        "scannedFiles": { "type": "integer" },
                        "stoppedEarly": { "type": "boolean" }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var result = DshJson.Deserialize<GlobResultValue>(value)
                                    ?? throw new JsonException("glob result value is malformed");
                    return [new TextBlock(RenderGlobPaths(result, resolved))];
                }),
            Execute = (args, exec) => Execute(args, exec, resolved),
        });
        return new CompositeDisposable(section, registration);
    }

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, GlobToolCaps caps)
    {
        var pattern = args.GetProperty("pattern").GetString() ?? "";
        if (pattern.Trim().Length == 0)
            throw new ArgumentException("pattern must be a non-empty string");
        var pathArg = args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        if (pathArg is not null && pathArg.Trim().Length == 0)
            throw new ArgumentException("path must be a non-empty string when given");
        var sessionCwd = exec.Agent?.Session.WorkingDirectory;
        var workdir = sessionCwd ?? Environment.CurrentDirectory;
        var root = pathArg is null
            ? workdir
            : Path.IsPathRooted(pathArg)
                ? Path.GetFullPath(pathArg)
                : Path.GetFullPath(Path.Combine(workdir, pathArg));
        var canonicalRoot = CanonicalFilePath.Canonicalize(root);
        workdir = root == workdir ? canonicalRoot : CanonicalFilePath.Canonicalize(workdir);
        root = canonicalRoot;
        if (!Directory.Exists(root))
            throw new HarnessException($"glob search failed (exit 2): the directory does not exist: \"{root}\"", "SEARCH_FAILED");
        var matcher = SearchFiles.CreateMatcher(pattern);
        var matches = new List<string>();
        var scanned = 0;
        var stoppedEarly = false;
        var deadline = Stopwatch.GetTimestamp();
        await foreach (var file in SearchFiles.EnumerateFiles(root, includeHidden: true, exec))
        {
            if (scanned >= caps.MaxScannedFiles || GrepTool.ElapsedMilliseconds(deadline) >= caps.TimeoutMs)
            {
                stoppedEarly = true;
                break;
            }
            scanned += 1;
            if (SearchFiles.Matches(matcher, Path.GetRelativePath(root, file)))
                matches.Add(file);
        }
        var ordered = matches
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path), Comparer<DateTime>.Default)
            .Select(path => SearchFiles.DisplayPath(workdir, path))
            .ToList();
        var displayRoot = pathArg is null ? "." : SearchFiles.DisplayPath(workdir, root);
        return new GlobResultValue(displayRoot, ordered, scanned, stoppedEarly);
    }

    internal static string RenderGlobPaths(GlobResultValue result, GlobToolCaps caps)
    {
        var paths = result.Paths;
        if (paths.Count == 0)
            return result.StoppedEarly
                ? $"No files found (stopped after scanning {result.ScannedFiles} files; narrow path or pattern)"
                : "No files found";
        if (paths.Count <= caps.MaxResults && !result.StoppedEarly)
            return string.Join('\n', paths);
        var reason = result.StoppedEarly
            ? $"search stopped after scanning {result.ScannedFiles} files"
            : "the result list was longer than the display cap";
        return $"{string.Join('\n', paths.Take(caps.MaxResults))}\n\n(Showing {Math.Min(paths.Count, caps.MaxResults)} of {paths.Count} paths; {reason} — narrow pattern or path to see more.)";
    }
}

public sealed record GrepToolCaps
{
    public int MaxMatches { get; init; } = 250;
    public int MaxLineBytes { get; init; } = 2000;
    public long TimeoutMs { get; init; } = 30_000;
    /** 超过这个大小的文件不参与内容搜索: 几百 MB 的日志读一遍就吃掉整次调用的预算。 */
    public long MaxFileBytes { get; init; } = 4 * 1024 * 1024;
}

public sealed record GrepMatchValue(string Path, int LineNumber, string Line);

public sealed record GrepResultValue(
    IReadOnlyList<GrepMatchValue> Matches,
    int ScannedFiles = 0,
    int SkippedFiles = 0,
    bool StoppedEarly = false,
    int SkippedDirectories = 0,
    bool TimedOut = false);

public static class GrepTool
{
    public const string ToolName = "grep";

    private const string SectionText = "Use the grep tool — not shell grep or rg — to search file contents. Use read on a matched file when you need surrounding context.";

    public static IDisposable Register(Context ctx, GrepToolCaps? caps = null)
    {
        var resolved = caps ?? new GrepToolCaps();
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:grep", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            MediatesFileAccess = true,
            Description = "Search file contents with a ripgrep regular expression. Returns matching lines with line numbers, grouped by file. "
                + $"Returns the first {resolved.MaxMatches} matches inline and stops there; files larger than {resolved.MaxFileBytes / (1024 * 1024)} MB and binary files are skipped. "
                + "A search without path prunes build-output directories (bin, obj, node_modules, artifacts*) and reports how many it pruned; name such a directory in path to search it anyway. "
                + "Use read on a matched file for surrounding context.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["pattern"] = ToolSchemas.StringParam("Regular expression to search for (ripgrep syntax)."),
                    ["path"] = ToolSchemas.StringParam("File or directory to search. Defaults to the session working directory; a relative path resolves against it."),
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
                        },
                        "scannedFiles": { "type": "integer" },
                        "skippedFiles": { "type": "integer" },
                        "skippedDirectories": { "type": "integer" },
                        "stoppedEarly": { "type": "boolean" },
                        "timedOut": { "type": "boolean" }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var result = DshJson.Deserialize<GrepResultValue>(value)
                                ?? throw new JsonException("grep result value is malformed");
                    var retained = RetainGrepMatches(result.Matches, resolved.MaxMatches, resolved.MaxLineBytes);
                    return [new TextBlock(FormatRetainedGrep(retained, result))];
                }),
            Execute = (args, exec) => Execute(args, exec, resolved),
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

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, GrepToolCaps caps)
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
        var sessionCwd = exec.Agent?.Session.WorkingDirectory;
        var workdir = sessionCwd ?? Environment.CurrentDirectory;
        var root = pathArg is null
            ? workdir
            : Path.IsPathRooted(pathArg)
                ? Path.GetFullPath(pathArg)
                : Path.GetFullPath(Path.Combine(workdir, pathArg));
        var requestedFileName = Path.GetFileName(root);
        var canonicalRoot = CanonicalFilePath.Canonicalize(root);
        workdir = root == workdir ? canonicalRoot : CanonicalFilePath.Canonicalize(workdir);
        root = canonicalRoot;
        var includeMatcher = include is null ? null : SearchFiles.CreateMatcher(include);
        var matches = new List<GrepMatchValue>();
        if (File.Exists(root))
        {
            // 显式指定的单个文件: 跳过大小与二进制预判, 用户点名要搜它。
            if (includeMatcher is null || SearchFiles.Matches(includeMatcher, requestedFileName))
                await ScanFile(root, regex, matches, workdir, exec, caps.MaxMatches)
                    .ConfigureAwait(false);
            return new GrepResultValue(matches, ScannedFiles: 1);
        }
        if (!Directory.Exists(root))
            throw new HarnessException($"grep search failed (exit 2): the path does not exist: \"{root}\"", "SEARCH_FAILED");
        var scanned = 0;
        var skipped = 0;
        var stoppedEarly = false;
        var timedOut = false;
        var deadline = Stopwatch.GetTimestamp();
        // 没点名目录时整棵剪掉构建产物: 仓库根下的 artifacts* 有几十 GB, 逐个文件读一遍就是几十秒的卡死。
        var pruner = pathArg is null ? DirectoryPruner.BuildOutputs() : null;
        // 边枚举边搜: 命中上限后立刻收场, 不再为了"排好序"先收集几十万个路径。
        await foreach (var file in SearchFiles.EnumerateFiles(root, includeHidden: false, exec, pruner).ConfigureAwait(false))
        {
            exec.Signal.ThrowIfCancellationRequested();
            // 墙钟预算: 剪枝只是让常见情形快, 这个才是"无论多大的树都不会卡住调用方"的保证。
            if (ElapsedMilliseconds(deadline) >= caps.TimeoutMs)
            {
                stoppedEarly = true;
                timedOut = true;
                break;
            }
            if (includeMatcher is not null && !SearchFiles.Matches(includeMatcher, Path.GetRelativePath(root, file)))
                continue;
            if (SearchFiles.LooksBinaryExtension(file) || SearchFiles.LengthOf(file) > caps.MaxFileBytes
                || await SearchFiles.LooksBinary(file, exec).ConfigureAwait(false))
            {
                skipped += 1;
                continue;
            }
            scanned += 1;
            await ScanFile(file, regex, matches, workdir, exec, caps.MaxMatches).ConfigureAwait(false);
            if (matches.Count >= caps.MaxMatches)
            {
                stoppedEarly = true;
                break;
            }
        }
        return new GrepResultValue(matches, scanned, skipped, stoppedEarly, pruner?.PrunedCount ?? 0, timedOut);
    }

    /** 自开始起的毫秒数(单调时钟, 不受系统时间跳变影响)。 */
    internal static long ElapsedMilliseconds(long startedAt) => (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private static async Task ScanFile(string file, Regex regex, List<GrepMatchValue> matches,
        string workdir, ToolRunContext exec, int limit)
    {
        var signal = exec.Signal;
        await using var access = await SafetyFileAccess.OpenReadAsync(file, exec).ConfigureAwait(false);
        using var reader = new StreamReader(access, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber += 1;
            if ((lineNumber & 0x3FF) == 0)
                signal.ThrowIfCancellationRequested();
            if (!regex.IsMatch(line))
                continue;
            matches.Add(new GrepMatchValue(SearchFiles.DisplayPath(workdir, file), lineNumber, line));
            if (matches.Count >= limit)
                return;
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

    private static string FormatRetainedGrep(RetainedMatches retained, GrepResultValue result)
    {
        var scan = ScanSummary(result);
        if (retained.Seen == 0) return scan is null ? "No matches found" : $"No matches found\n\n{scan}";
        return scan is null ? FormatGrepOutput(retained) : $"{FormatGrepOutput(retained)}\n\n{scan}";
    }

    /** 只在"这次搜索被裁剪过"时说明裁剪理由, 否则纯属噪声。 */
    private static string? ScanSummary(GrepResultValue result)
    {
        if (result.SkippedFiles == 0 && result.SkippedDirectories == 0 && !result.StoppedEarly) return null;
        var parts = new List<string> { $"scanned {result.ScannedFiles} files" };
        if (result.SkippedFiles > 0) parts.Add($"skipped {result.SkippedFiles} binary or oversized files");
        if (result.SkippedDirectories > 0)
            parts.Add($"pruned {result.SkippedDirectories} build-output {(result.SkippedDirectories == 1 ? "directory" : "directories")} (pass path to search them)");
        if (result.TimedOut) parts.Add("stopped at the time budget (narrow path or pattern)");
        else if (result.StoppedEarly) parts.Add($"stopped after reaching {result.Matches.Count} matches");
        return $"({string.Join(", ", parts)})";
    }
}
