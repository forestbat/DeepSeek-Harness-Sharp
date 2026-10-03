using System.Text;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record MemoryOpResult(string Action, string Key, string Section, bool Replaced);

public sealed record SessionDigest(SessionId Id, string Topic, string Summary, DateTimeOffset? UpdatedAt);

public sealed record MemoryAuditEntry(string At, string Action, string Key, string Section, string? Text, string Source);

public sealed record MemoryForgetRequest(IReadOnlyList<string>? Keys, string? Query, string? Section, bool DryRun);

public sealed record MemoryForgetResult(string Action, IReadOnlyList<string> Keys, IReadOnlyList<string> Sections, int Count, bool DryRun);

public sealed record MemoryMatch(string Key, string Section, string Text);

/** 项目记忆的记录级视图:在 IMemoryStore 的整段文本之上提供 remember/correct/forget、注入索引、会话摘要与审计。 */
public sealed class ProjectMemory
{
    public const int DefaultIndexBudgetBytes = 8192;
    public const int MaxRecentDigests = 5;
    public const int ShowDigestsLimit = 20;
    public const int MinQueryLength = 2;
    public const string FactsSection = "Facts";
    public const string CorrectionsSection = "Corrections";
    public const string SidecarDirName = ".dsh-memory";

    private static readonly MemoryKind[] KindPriority =
    [
        MemoryKind.Correction,
        MemoryKind.Decision,
        MemoryKind.Constraint,
        MemoryKind.Fact,
        MemoryKind.Environment,
    ];

    private readonly IMemoryStore _store;
    private readonly string _sidecarDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectMemory(IMemoryStore store, string sidecarDir)
    {
        _store = store;
        _sidecarDir = sidecarDir;
    }

    public string Description => _store.Description;

    public static string SidecarDirFor(string projectRoot) => Path.Combine(projectRoot, SidecarDirName);

    private string SessionsDir => Path.Combine(_sidecarDir, "sessions");

    private string AuditPath => Path.Combine(_sidecarDir, "decisions.jsonl");

    public Task<MemoryOpResult> RememberAsync(string key, string text, string? section, string source, CancellationToken cancellationToken = default)
        => UpsertAsync("remember", key, text, string.IsNullOrWhiteSpace(section) ? FactsSection : section.Trim(), source, cancellationToken);

    public Task<MemoryOpResult> CorrectAsync(string key, string text, string source, CancellationToken cancellationToken = default)
        => UpsertAsync("correct", key, text, CorrectionsSection, source, cancellationToken);

    public async Task<MemoryOpResult> ForgetAsync(string key, string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("memory forget requires a non-empty key");
        var result = await ForgetAsync(new MemoryForgetRequest([key.Trim()], null, null, false), source, cancellationToken);
        return new MemoryOpResult("forget", key.Trim(), result.Sections.Count > 0 ? result.Sections[0] : "", true);
    }

    /** 批量/条件删除:按 keys 精确匹配 key,或按 query 在正文中做大小写不敏感子串匹配;`section` 为可选节过滤;`dryRun` 只返回命中不落盘。 */
    public async Task<MemoryForgetResult> ForgetAsync(MemoryForgetRequest request, string source, CancellationToken cancellationToken = default)
    {
        var keys = NormalizeKeys(request.Keys);
        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (keys.Count == 0 && query is null)
            throw new InvalidOperationException("memory forget requires keys or a query");
        if (query is not null && query.Length < MinQueryLength)
            throw new InvalidOperationException($"memory forget query must be at least {MinQueryLength} characters");
        var section = string.IsNullOrWhiteSpace(request.Section) ? null : request.Section.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var doc = await LoadAsync(cancellationToken);
            var removed = doc.RemoveWhere(section, record => Matches(record, keys, query));
            if (removed.Count == 0 && !request.DryRun)
                throw new InvalidOperationException(DescribeMiss(keys, query, section));
            if (!request.DryRun && removed.Count > 0)
            {
                await SaveAsync(doc, cancellationToken);
                foreach (var (removedSection, record) in removed)
                    await AuditAsync(new MemoryAuditEntry(Now(), "forget", record.Key, removedSection, null, source), cancellationToken);
            }
            return new MemoryForgetResult(
                "forget",
                [.. removed.Select(entry => entry.Record.Key)],
                [.. removed.Select(entry => entry.Section).Distinct(StringComparer.OrdinalIgnoreCase)],
                removed.Count,
                request.DryRun);
        }
        finally
        {
            _gate.Release();
        }
    }

    /** 只读检索:返回 key/section/text 命中,不修改文件。 */
    public async Task<IReadOnlyList<MemoryMatch>> FindAsync(string query, string? section, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < MinQueryLength)
            throw new InvalidOperationException($"memory find query must be at least {MinQueryLength} characters");
        var trimmed = query.Trim();
        var sectionFilter = string.IsNullOrWhiteSpace(section) ? null : section.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var doc = await LoadAsync(cancellationToken);
            var matches = new List<MemoryMatch>();
            foreach (var current in doc.Sections)
            {
                if (sectionFilter is not null && !string.Equals(current.Name, sectionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                matches.AddRange(current.Records
                    .Where(record => Matches(record, [], trimmed))
                    .Select(record => new MemoryMatch(record.Key, current.Name, record.Text)));
            }
            return matches;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<string> NormalizeKeys(IReadOnlyList<string>? keys)
        => keys is null
            ? []
            : [.. keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim())];

    private static bool Matches(MemoryRecord record, IReadOnlyList<string> keys, string? query)
        => keys.Count > 0
            ? keys.Any(key => string.Equals(key, record.Key, StringComparison.OrdinalIgnoreCase))
            : record.Text.Contains(query!, StringComparison.OrdinalIgnoreCase);

    private static string DescribeMiss(IReadOnlyList<string> keys, string? query, string? section)
    {
        var selector = keys.Count > 0 ? $"key \"{string.Join(", ", keys)}\"" : $"query \"{query}\"";
        return section is null
            ? $"no memory record matching {selector}"
            : $"no memory record matching {selector} in section \"{section}\"";
    }

    public async Task<string> BuildIndexAsync(int budgetBytes = DefaultIndexBudgetBytes, CancellationToken cancellationToken = default)
    {
        MemoryDocument doc;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            doc = await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        var digests = RecentDigests(MaxRecentDigests);
        if (doc.Sections.All(section => !section.Records.Any()) && digests.Count == 0)
            return $"Project memory ({Description}) is empty; record project knowledge with memory_save when you learn it.";
        var lines = new List<string> { $"Project memory ({Description}):" };
        foreach (var section in OrderedSections(doc))
        {
            lines.Add("");
            lines.Add($"## {section.Name}");
            lines.AddRange(section.Records.Select(record => record.Render()));
        }
        if (digests.Count > 0)
        {
            lines.Add("");
            lines.Add("## Recent sessions");
            foreach (var digest in digests)
                lines.Add($"- [{digest.Topic}] {digest.Summary} ({digest.UpdatedAt:yyyy-MM-dd})");
        }
        return TruncateToBudget(lines, budgetBytes);
    }

    public async Task<string> ShowAsync(CancellationToken cancellationToken = default)
    {
        string? text;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            text = await _store.GetAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(Description);
        builder.AppendLine(string.IsNullOrWhiteSpace(text) ? "(empty)" : text.TrimEnd());
        var digests = RecentDigests(ShowDigestsLimit);
        if (digests.Count > 0)
        {
            builder.AppendLine().AppendLine("## Session digests");
            foreach (var digest in digests)
                builder.AppendLine($"- {digest.Id.Value}: [{digest.Topic}] {digest.Summary} ({digest.UpdatedAt:yyyy-MM-dd})");
        }
        return builder.ToString();
    }

    public async Task WriteDigestAsync(SessionId id, string topic, string summary, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var doc = new MemoryDocument();
        doc.Preamble.Add($"# Session {id.Value}");
        doc.Upsert("Digest", new MemoryRecord("topic", topic, now));
        doc.Upsert("Digest", new MemoryRecord("summary", summary, now));
        Directory.CreateDirectory(SessionsDir);
        var path = DigestPath(id);
        var temporary = $"{path}.tmp";
        await File.WriteAllTextAsync(temporary, doc.Render(), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyList<SessionDigest> RecentDigests(int max)
    {
        if (!Directory.Exists(SessionsDir))
            return [];
        return Directory.EnumerateFiles(SessionsDir, "*.md")
            .Select(path => (Path: path, WrittenAt: File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(entry => entry.WrittenAt)
            .Take(max)
            .Select(entry => ParseDigest(entry.Path))
            .OfType<SessionDigest>()
            .ToList();
    }

    private async Task<MemoryOpResult> UpsertAsync(string action, string key, string text, string section, string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"memory {action} requires a non-empty key");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"memory {action} requires non-empty text");
        var trimmedKey = key.Trim();
        if (trimmedKey.Contains("::"))
            throw new InvalidOperationException("memory key must not contain \"::\"");
        var record = new MemoryRecord(trimmedKey, FirstLine(text), DateTimeOffset.UtcNow);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var doc = await LoadAsync(cancellationToken);
            var replaced = doc.Upsert(section, record);
            await SaveAsync(doc, cancellationToken);
            await AuditAsync(new MemoryAuditEntry(Now(), action, record.Key, section, record.Text, source), cancellationToken);
            return new MemoryOpResult(action, record.Key, section, replaced);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryDocument> LoadAsync(CancellationToken cancellationToken)
        => MemoryDocument.Parse(await _store.GetAsync(cancellationToken));

    private Task SaveAsync(MemoryDocument doc, CancellationToken cancellationToken)
        => _store.SetAsync(doc.Render(), cancellationToken);

    private async Task AuditAsync(MemoryAuditEntry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_sidecarDir);
        var line = JsonSerializer.Serialize(entry, DshCoreJsonContext.Default.MemoryAuditEntry);
        await File.AppendAllTextAsync(AuditPath, line + "\n", cancellationToken);
    }

    private SessionDigest? ParseDigest(string path)
    {
        var doc = MemoryDocument.Parse(File.ReadAllText(path));
        var records = doc.FindSection("Digest")?.Records.ToList();
        var topic = records?.FirstOrDefault(record => record.Key == "topic");
        var summary = records?.FirstOrDefault(record => record.Key == "summary");
        if (topic is null || summary is null)
            return null;
        var id = Path.GetFileNameWithoutExtension(path);
        return new SessionDigest(SessionId.Create(id), topic.Text, summary.Text, summary.UpdatedAt ?? topic.UpdatedAt);
    }

    private string DigestPath(SessionId id) => Path.Combine(SessionsDir, $"{SafeFileName(id.Value)}.md");

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Select(c => invalid.Contains(c) ? '_' : c)]);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return (newline < 0 ? trimmed : trimmed[..newline]).TrimEnd('\r');
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString(MemoryRecord.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<MemoryDocument.Section> OrderedSections(MemoryDocument doc)
        => doc.Sections
            .Where(section => section.Records.Any())
            .OrderBy(section =>
            {
                var kind = MemoryDocument.KindOf(section.Name);
                var priority = Array.IndexOf(KindPriority, kind);
                return priority < 0 ? KindPriority.Length : priority;
            });

    private static string TruncateToBudget(List<string> lines, int budgetBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var line in lines)
        {
            var cost = Encoding.UTF8.GetByteCount(line) + 1;
            if (used + cost > budgetBytes)
            {
                builder.AppendLine("… (truncated)");
                break;
            }
            builder.AppendLine(line);
            used += cost;
        }
        return builder.ToString().TrimEnd();
    }
}
