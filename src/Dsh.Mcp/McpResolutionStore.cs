using System.Text.Json;
using Dsh.Boot;

namespace Dsh.Mcp;

public sealed class McpResolutionStore(HarnessHome? home = null)
{
    private const string FileName = "mcp_resolutions.json";
    private readonly Lock _gate = new();
    private readonly List<McpConflictResolution> _resolutions = [];
    private readonly string? _filePath = home is not null ? Path.Combine(home.Root, FileName) : null;

    public IReadOnlyList<McpConflictResolution> Resolutions
    {
        get
        {
            lock (_gate)
                return _resolutions.ToList();
        }
    }

    public async Task InitializeAsync(CancellationToken signal = default)
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var loaded = await JsonSerializer.DeserializeAsync<List<McpConflictResolution>>(stream, cancellationToken: signal);
            if (loaded is not null)
            {
                lock (_gate)
                {
                    _resolutions.Clear();
                    _resolutions.AddRange(loaded);
                }
            }
        }
        catch
        {
            // 文件损坏或读取失败时沿用空列表并等待后续覆盖。
        }
    }

    public async Task<IReadOnlyList<McpConflictResolution>> SyncAsync(
        IReadOnlyList<McpConflict> conflicts,
        CancellationToken signal = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            var existingById = _resolutions.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var merged = new List<McpConflictResolution>();
            foreach (var conflict in conflicts)
            {
                if (existingById.TryGetValue(conflict.ConflictId, out var existing))
                {
                    merged.Add(existing);
                }
                else
                {
                    merged.Add(new McpConflictResolution(
                        conflict.ConflictId,
                        conflict.Kind,
                        conflict.ServerNames,
                        conflict.ToolNames,
                        conflict.Description,
                        conflict.SuggestedAction,
                        conflict.TargetServerOrTool,
                        McpResolutionStatus.Pending,
                        now));
                }
            }
            _resolutions.Clear();
            _resolutions.AddRange(merged);
        }
        await SaveAsync(signal);
        return Resolutions;
    }

    public async Task<bool> ApproveAsync(string resolutionId, CancellationToken signal = default)
        => await UpdateStatusAsync(resolutionId, McpResolutionStatus.Approved, signal);

    public async Task<bool> RejectAsync(string resolutionId, CancellationToken signal = default)
        => await UpdateStatusAsync(resolutionId, McpResolutionStatus.Rejected, signal);

    public bool IsToolDisabled(string server, string tool)
    {
        lock (_gate)
        {
            return _resolutions.Any(r => r.Status == McpResolutionStatus.Approved
                && r.Action == McpResolutionAction.DisableTool
                && r.ServerNames.Contains(server, StringComparer.OrdinalIgnoreCase)
                && (string.Equals(r.Target, tool, StringComparison.OrdinalIgnoreCase)
                    || r.ToolNames.Contains(tool, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public string FormatDiff()
    {
        lock (_gate)
        {
            if (_resolutions.Count == 0)
                return "当前无检测到的 MCP 语义冲突或消解建议。";
            var pending = _resolutions.Where(r => r.Status == McpResolutionStatus.Pending).ToList();
            var approved = _resolutions.Where(r => r.Status == McpResolutionStatus.Approved).ToList();
            var rejected = _resolutions.Where(r => r.Status == McpResolutionStatus.Rejected).ToList();
            var lines = new List<string>
            {
                $"MCP 语义治理建议总数: {_resolutions.Count} (待处理: {pending.Count}, 已批准: {approved.Count}, 已驳回: {rejected.Count})",
            };
            AppendGroup(lines, "待审批建议", pending);
            AppendGroup(lines, "已批准建议", approved);
            AppendGroup(lines, "已驳回建议", rejected);
            return string.Join('\n', lines);
        }
    }

    private static void AppendGroup(List<string> lines, string header, List<McpConflictResolution> group)
    {
        if (group.Count == 0)
            return;
        lines.Add($"\n--- {header} ---");
        foreach (var item in group)
            lines.Add($"[{item.Id}] ({item.Kind}) 建议操作: {item.Action} 目标: {item.Target}\n  说明: {item.Description}");
    }

    private async Task<bool> UpdateStatusAsync(
        string resolutionId,
        McpResolutionStatus newStatus,
        CancellationToken signal)
    {
        var found = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            for (var i = 0; i < _resolutions.Count; i++)
            {
                if (string.Equals(_resolutions[i].Id, resolutionId, StringComparison.Ordinal))
                {
                    _resolutions[i] = _resolutions[i] with { Status = newStatus, DecidedAt = now };
                    found = true;
                    break;
                }
            }
        }
        if (found)
            await SaveAsync(signal);
        return found;
    }

    private async Task SaveAsync(CancellationToken signal)
    {
        if (_filePath is null)
            return;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        List<McpConflictResolution> snapshot;
        lock (_gate)
            snapshot = _resolutions.ToList();
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true }, signal);
    }
}
