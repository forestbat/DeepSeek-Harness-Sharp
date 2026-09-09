namespace Cordis.Loader;

public sealed class IncludeConfig
{
    public required string Path { get; init; }
    public List<object?>? Initial { get; init; }
    public List<Dictionary<string, object?>>? Patches { get; init; }
    public bool? EnableLogs { get; init; }

    public static IncludeConfig From(object? config)
    {
        if (config is not IDictionary<string, object?> dict)
        {
            throw new CordisException("INVALID_CONFIG", "include config must be a mapping");
        }
        return new IncludeConfig
        {
            Path = dict["path"] as string ?? throw new CordisException("INVALID_CONFIG", "include config requires path"),
            Initial = dict.GetOrNull("initial") as List<object?>,
            Patches = (dict.GetOrNull("patches") as List<object?>)?
                .OfType<Dictionary<string, object?>>().ToList(),
            EnableLogs = dict.GetOrNull("enableLogs") as bool?,
        };
    }
}

[Inject("loader")]
public class Include : EntryTree, IAsyncInit
{
    private static readonly Dictionary<string, string> Writable = new()
    {
        [".json"] = "application/json",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
    };

    private IncludeConfig _config;
    private readonly string _filename;
    private readonly string _type;
    private readonly IncludeJournal _journal = new();
    private IncludePatchIndex _patchIndex;
    private bool _readonly;
    private string? _content;
    private List<object?>? _data;
    private List<EntryOptions>? _appliedTree;
    private Task? _flushTask;

    public Include(Context ctx, object? config) : base(ctx)
    {
        _config = IncludeConfig.From(config);
        EnableLogs = _config.EnableLogs
            ?? ctx.Fiber.Entry?.Parent.Tree.EnableLogs
            ?? false;
        var baseUrl = ctx.BaseUrl ?? throw new CordisException("NO_BASE_URL", "include requires baseUrl");
        _filename = new Uri(new Uri(baseUrl), _config.Path).LocalPath;
        var ext = Path.GetExtension(_filename);
        if (!Writable.TryGetValue(ext, out var type))
        {
            throw new CordisException("UNSUPPORTED_EXTENSION", $"extension \"{ext}\" not supported");
        }
        _type = type;
        _patchIndex = new IncludePatchIndex(_config.Patches);

        ctx.On("internal/update", async (thisArg, args) =>
        {
            ValueTask<object?> Next() => args[2] switch
            {
                Func<ValueTask<object?>> asyncNext => asyncNext(),
                Func<object?> syncNext => new ValueTask<object?>(syncNext()),
                _ => throw new InvalidOperationException("invalid next"),
            };
            var path = (args[0] as IDictionary<string, object?>)?.GetOrNull("path") as string;
            if (path != _config.Path) return await Next();
            _config = IncludeConfig.From(args[0]!);
            _patchIndex = new IncludePatchIndex(_config.Patches);
            if (_data is not null)
            {
                var tree = ApplyPatches(_data.Select(EntryOptions.From).ToList(), _config.Patches, Warn);
                tree = _journal.Apply(tree);
                _appliedTree = tree.Select(IncludeJournal.CloneEntry).ToList();
                await Root.Update(tree);
            }
            return await Next();
        });
    }

    private async Task<bool> Read(bool forced = false)
    {
        var content = await File.ReadAllTextAsync(_filename);
        if (!forced && _content == content) return false;
        _content = content;
        _data = _type switch
        {
            "application/yaml" => YamlConfig.Load(content),
            "application/json" => System.Text.Json.JsonSerializer.Deserialize<List<object?>>(content),
            _ => throw new CordisException("UNSUPPORTED_TYPE", $"type {_type} not supported"),
        };
        CheckAccess();
        return true;
    }

    private void CheckAccess()
    {
        try
        {
            using var stream = File.Open(_filename, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        catch
        {
            _readonly = true;
        }
    }

    public async IAsyncEnumerable<object?> Init()
    {
        try
        {
            await Read();
        }
        catch
        {
            if (_config.Initial is not null)
            {
                await WriteFile(_config.Initial);
                await Read();
            }
            else
            {
                throw new CordisException("CONFIG_NOT_FOUND", $"config file not found: {_filename}");
            }
        }

        yield return (Action)(() => Stop());

        var patched = ApplyPatches(
            (_data ?? []).Select(EntryOptions.From).ToList(),
            _config.Patches,
            Warn);
        await Root.Update(patched);
        _appliedTree = patched.Select(IncludeJournal.CloneEntry).ToList();
    }

    public void Stop()
    {
        Root.Stop();
    }

    public async Task Refresh()
    {
        var oldData = _data?.Select(EntryOptions.From).ToList() ?? [];
        if (!await Read()) return;
        var newData = _data!.Select(EntryOptions.From).ToList();
        var conflicts = _journal.Reconcile(
            IncludeJournal.Flatten(oldData),
            IncludeJournal.Flatten(newData),
            (id, key) => _patchIndex.FileOwned(id, key));
        foreach (var conflict in conflicts)
        {
            Ctx.Root.Logger.Invoke("loader").Error(
                "config conflict in %C: entry %C %s; file wins",
                _filename, conflict.Id, conflict.Reason);
        }
        var tree = ApplyPatches(newData, _config.Patches, Warn);
        tree = _journal.Apply(tree);
        _appliedTree = tree.Select(IncludeJournal.CloneEntry).ToList();
        await Root.Update(tree);
    }

    private async Task WriteFile(List<object?> config)
    {
        if (_readonly)
        {
            throw new CordisException("READONLY_CONFIG", "cannot overwrite readonly config");
        }
        _content = _type switch
        {
            "application/yaml" => YamlConfig.Dump(config),
            "application/json" => System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            _ => throw new CordisException("UNSUPPORTED_TYPE", $"type {_type} not supported"),
        };
        await IncludeFileWriter.WriteAtomicAsync(_filename, _content);
    }

    private void Warn(string message, object?[] args)
    {
        Ctx.Root.Logger.Invoke("loader").Warn(message, args);
    }

    public override void Write()
    {
        Ctx.Events.Emit(null, "loader/config-update");
        RecordJournal();
        _ = FlushAsync();
    }

    public Task FlushAsync()
    {
        if (_flushTask is { IsCompleted: false }) return _flushTask;
        _flushTask = FlushInternalAsync();
        return _flushTask;
    }

    private void RecordJournal()
    {
        var current = Root.Data.Select(IncludeJournal.CloneEntry).ToList();
        if (_appliedTree is null)
        {
            _appliedTree = current;
            return;
        }
        var oldMap = IncludeJournal.Flatten(_appliedTree);
        var newMap = IncludeJournal.Flatten(current);
        foreach (var (id, flat) in newMap)
        {
            if (!oldMap.TryGetValue(id, out var oldFlat))
            {
                _journal.RecordAdd(flat.Options, flat.Parent, flat.Position);
            }
            else if (!CordisUtils.DeepEqual(oldFlat.Options, flat.Options) || oldFlat.Parent != flat.Parent)
            {
                _journal.RecordUpdate(id, oldFlat.Options, flat.Options, flat.Parent, flat.Position, oldFlat.Parent != flat.Parent);
            }
        }
        foreach (var id in oldMap.Keys)
        {
            if (!newMap.ContainsKey(id)) _journal.RecordRemove(id);
        }
        _appliedTree = current;
    }

    private async Task FlushInternalAsync()
    {
        while (!_journal.IsEmpty)
        {
            var batch = _journal.TakeSnapshot();
            if (batch.Count == 0) break;
            var data = _data?.Select(EntryOptions.From).ToList() ?? [];
            var patches = _config.Patches?.Select(ClonePatch).ToList() ?? [];
            var result = IncludePatch.RouteJournal(batch, data, patches, Warn);
            if (result.PatchesChanged)
            {
                _config = new IncludeConfig
                {
                    Path = _config.Path,
                    Initial = _config.Initial,
                    Patches = patches,
                    EnableLogs = _config.EnableLogs,
                };
            }
            if (result.FileChanged)
            {
                var expected = _content;
                var current = await File.ReadAllTextAsync(_filename);
                if (expected is not null && current != expected)
                {
                    _journal.MergeBack(batch);
                    await Refresh();
                    continue;
                }
                var fileData = data.Select(IncludeJournal.CloneEntry).ToList();
                var text = Dump(fileData);
                await IncludeFileWriter.WriteAtomicAsync(_filename, text, expected);
                _content = text;
                _data = fileData.Cast<object?>().ToList();
            }
        }
    }

    private static Dictionary<string, object?> ClonePatch(Dictionary<string, object?> patch)
    {
        return patch.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value));
    }

    private static object? CloneValue(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dict => dict.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value)),
            List<object?> list => list.Select(CloneValue).ToList(),
            _ => value,
        };
    }

    private string Dump(List<EntryOptions> data)
    {
        var config = data.Cast<object?>().ToList();
        return _type switch
        {
            "application/yaml" => YamlConfig.Dump(config),
            "application/json" => System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            _ => throw new CordisException("UNSUPPORTED_TYPE", $"type {_type} not supported"),
        };
    }

    public static List<EntryOptions> ApplyPatches(
        List<EntryOptions> data,
        List<Dictionary<string, object?>>? patches,
        Action<string, object?[]> warn)
    {
        if (patches is null || patches.Count == 0) return data;

        var entryMap = new Dictionary<string, EntryOptions>();
        void BuildMap(IEnumerable<EntryOptions> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Id is not null) entryMap[entry.Id] = entry;
                if (entry.Group && entry.Config is List<object?> children)
                {
                    BuildMap(children.Select(EntryOptions.From));
                }
            }
        }
        BuildMap(data);

        foreach (var patch in patches)
        {
            var id = patch.GetOrNull("id") as string;
            var insert = (patch.GetOrNull("insert") as List<object?>)?.Select(EntryOptions.From).ToList();
            var name = patch.GetOrNull("name") as string;

            if (insert is not null)
            {
                if (id is not null)
                {
                    if (!entryMap.TryGetValue(id, out var target))
                    {
                        warn("patch insert: entry %C not found", [id]);
                        continue;
                    }
                    if (!target.Group)
                    {
                        warn("patch insert: entry %C is not a group", [id]);
                        continue;
                    }
                    if (target.Config is not List<object?> list)
                    {
                        list = [];
                        target.Config = list;
                    }
                    list.AddRange(insert.Cast<object?>());
                }
                else
                {
                    data.AddRange(insert);
                }
                continue;
            }

            if (id is null)
            {
                warn("patch: id is required for non-insert patches", []);
                continue;
            }

            if (!entryMap.TryGetValue(id, out var entry))
            {
                warn("patch: entry %C not found", [id]);
                continue;
            }

            if (name is not null && name != entry.Name)
            {
                warn("patch: name mismatch for %C (expected %C, got %C), skipping", [id, entry.Name, name]);
                continue;
            }

            foreach (var (key, value) in patch)
            {
                if (key is "id" or "insert" or "name") continue;
                entry[key] = value;
            }
        }

        return data;
    }
}
