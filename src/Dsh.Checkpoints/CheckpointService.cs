using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Checkpoints;

public sealed record CheckpointPolicy
{
    public const int DefaultMaxPoints = 256;
    public const int DefaultKeepDays = 15;

    public required int MaxPoints { get; init; }
    public required int KeepDays { get; init; }

    /** config 即 plugins."@deepseek-ai/dsh-checkpoints" 的参数表(可空);功能的开关即插件自身的 enabled。 */
    public static CheckpointPolicy Resolve(object? config)
    {
        var maxPoints = DefaultMaxPoints;
        var keepDays = DefaultKeepDays;
        if (config is IReadOnlyDictionary<string, object?> map)
        {
            maxPoints = map.TryGetValue("max_points", out var maxValue) && maxValue is long max ? (int)max : maxPoints;
            keepDays = map.TryGetValue("keep_days", out var keepValue) && keepValue is long keep ? (int)keep : keepDays;
        }
        return new CheckpointPolicy
        {
            MaxPoints = Math.Max(1, maxPoints),
            KeepDays = Math.Max(1, keepDays),
        };
    }
}

public sealed class CheckpointService : Service, ICheckpointRestore, IDisposable
{
    public const string ServiceName = "checkpoints";

    private sealed record ProjectRepo(ShadowGit Git, CheckpointLog Log);

    private sealed record SnapshotRequest(SessionId Session, long Seq, string Cwd, string Reason);

    private readonly CheckpointPolicy _policy;
    private readonly string _homeRoot;
    private readonly Dictionary<string, ProjectRepo> _repos = new(StringComparer.Ordinal);
    private readonly Channel<SnapshotRequest> _queue = Channel.CreateBounded<SnapshotRequest>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Lock _sync = new();

    public CheckpointService(Context ctx, CheckpointPolicy policy, string homeRoot) : base(ctx, ServiceName)
    {
        _policy = policy;
        _homeRoot = homeRoot;
        _ = ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new InvalidOperationException("checkpoints requires the sessionStore service");
        _worker = Task.Run(ProcessAsync);
        ctx.On<SessionEventNotification>(
            notification => Observe(notification.Session, notification.Event),
            new EventOptions { Global = true });
    }

    public int MaxPoints => _policy.MaxPoints;

    public int KeepDays => _policy.KeepDays;

    public IReadOnlyList<CheckpointPoint> PointsFor(string cwd)
        => RepoForOrNull(cwd)?.Log.Points ?? [];

    public async Task<string> RestoreFilesAsync(string cwd, int index, CancellationToken signal)
    {
        var repo = RepoForOrNull(cwd)
            ?? throw new InvalidOperationException("no checkpoints recorded for this project");
        var points = repo.Log.Points;
        if (index < 0 || index >= points.Count)
            throw new InvalidOperationException($"checkpoint index out of range: {index} (have {points.Count})");
        var point = points[index];
        await repo.Git.RestoreAsync(point.Commit, signal);
        return point.Commit;
    }

    /** D3 revert 的文件侧: 恢复到 Seq <= seq 的最近检查点; 没有则返回 null。 */
    public async Task<string?> RestoreToSeqAsync(string cwd, long seq, CancellationToken signal)
    {
        var points = PointsFor(cwd);
        var index = -1;
        for (var position = 0; position < points.Count; position++)
        {
            if (points[position].Seq > seq)
                break;
            index = position;
        }
        return index < 0 ? null : await RestoreFilesAsync(cwd, index, signal);
    }

    private void Observe(Session session, SessionEvent sessionEvent)
    {
        var reason = sessionEvent.Data switch
        {
            ToolResultPayload => "tool/result",
            TurnEndPayload => "turn/end",
            _ => null,
        };
        if (reason is null)
            return;
        var cwd = session.Header.Cwd;
        if (cwd is not { Length: > 0 })
            return;
        _queue.Writer.TryWrite(new SnapshotRequest(session.Header.Id, sessionEvent.Seq, cwd, reason));
    }

    private async Task ProcessAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token))
            {
                SnapshotRequest? request = null;
                while (reader.TryRead(out var item))
                    request = item;
                if (request is null)
                    continue;
                try
                {
                    await TakeSnapshotAsync(request, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error)
                {
                    Ctx.Logger.Error("%s", error);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TakeSnapshotAsync(SnapshotRequest request, CancellationToken signal)
    {
        var repo = RepoFor(request.Cwd);
        await repo.Git.InitAsync(signal);
        var tree = await repo.Git.StageTreeAsync(signal);
        var last = repo.Log.Points.LastOrDefault();
        if (last is not null && await repo.Git.TreeOfAsync(last.Commit, signal) == tree)
            return;
        var message = $"checkpoint session={request.Session.Value} seq={request.Seq} reason={request.Reason} at={DateTimeOffset.Now:O}";
        var commit = await repo.Git.CommitTreeAsync(tree, message, signal);
        repo.Log.Append(new CheckpointPoint
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Commit = commit,
            Seq = request.Seq,
            Session = request.Session.Value,
            Reason = request.Reason,
        });
        var removed = repo.Log.Prune(_policy.MaxPoints, _policy.KeepDays, DateTimeOffset.UtcNow);
        if (removed.Count > 0)
        {
            await repo.Git.DropAsync(removed.Select(point => point.Commit), signal);
            Ctx.Logger.Info("%s", $"checkpoints pruned {removed.Count} point(s) for {request.Cwd}");
        }
    }

    private ProjectRepo RepoFor(string cwd)
    {
        lock (_sync)
        {
            if (_repos.TryGetValue(cwd, out var existing))
                return existing;
            var root = Path.Combine(new HarnessHome(_homeRoot).CheckpointsPath, ProjectStorageKey.BoundedOf(cwd));
            var created = new ProjectRepo(
                new ShadowGit(Path.Combine(root, "repo.git"), cwd),
                new CheckpointLog(Path.Combine(root, "points.jsonl")));
            _repos[cwd] = created;
            return created;
        }
    }

    private ProjectRepo? RepoForOrNull(string cwd)
    {
        var repo = RepoFor(cwd);
        return repo.Log.Points.Count == 0 && !Directory.Exists(repo.Git.GitDir) ? null : repo;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
