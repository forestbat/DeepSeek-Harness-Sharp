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

    private readonly IncludeConfig _config;
    private readonly string _filename;
    private readonly string _type;
    private bool _readonly;
    private string? _content;
    private List<object?>? _data;
    private CancellationTokenSource _writeCts = new();

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

        ctx.On("internal/update", (thisArg, args) =>
        {
            var next = (Func<object?>)args[2]!;
            var path = (args[0] as IDictionary<string, object?>)?.GetOrNull("path") as string;
            if (path != _config.Path) return new ValueTask<object?>(next());
            if (_data is not null) _ = Root.Update(_data.Select(EntryOptions.From).ToList());
            return new ValueTask<object?>();

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
            (message, args) => Ctx.Root.Logger.Invoke("loader").Warn(message, args));
        await Root.Update(patched);
    }

    public void Stop()
    {
        Root.Stop();
    }

    public async Task Refresh()
    {
        if (!await Read()) return;
        if (_data is not null) await Root.Update(_data.Select(EntryOptions.From).ToList());
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
        await File.WriteAllTextAsync(_filename + ".tmp", _content);
        File.Move(_filename + ".tmp", _filename, true);
    }

    public override void Write()
    {
        Ctx.Events.Emit(null, "loader/config-update");
        _writeCts.Cancel();
        _writeCts = new CancellationTokenSource();
        var token = _writeCts.Token;
        var config = Root.Data.Select(o => (object?)o).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(0, token);
                await WriteFile(config);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                Ctx.Logger.Error("%s", error);
            }
        });
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
