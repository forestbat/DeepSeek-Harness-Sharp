namespace Cordis.Loader;

public enum IncludeJournalKind
{
    Add,
    Update,
    Remove,
    Move,
}

public sealed class IncludeJournalRecord
{
    public required IncludeJournalKind Kind { get; set; }
    public bool Created { get; set; }
    public bool Relocated { get; set; }
    public string? Parent { get; set; }
    public int Position { get; set; }
    public Dictionary<string, object?> Changes { get; set; } = new();
}

public sealed record IncludeConflict(string Id, string Reason);

public sealed record IncludeFlatEntry(string? Parent, int Position, EntryOptions Options, System.Collections.IList List);

public sealed class IncludeJournal
{
    public Dictionary<string, IncludeJournalRecord> Records { get; } = new();

    public bool IsEmpty => Records.Count == 0;

    public void RecordAdd(EntryOptions options, string? parent, int position)
    {
        var id = options.Id ?? throw new CordisException("INVALID_CONFIG", "cannot journal an entry without id");
        Records[id] = new IncludeJournalRecord
        {
            Kind = IncludeJournalKind.Add,
            Created = true,
            Relocated = true,
            Parent = parent,
            Position = position,
            Changes = Diff(null, options),
        };
    }

    public void RecordUpdate(string id, EntryOptions legacy, EntryOptions options, string? parent, int position, bool moved = false)
    {
        Records[id] = new IncludeJournalRecord
        {
            Kind = moved ? IncludeJournalKind.Move : IncludeJournalKind.Update,
            Created = false,
            Relocated = moved,
            Parent = parent,
            Position = position,
            Changes = Diff(legacy, options),
        };
    }

    public void RecordRemove(string id)
    {
        Records[id] = new IncludeJournalRecord { Kind = IncludeJournalKind.Remove };
    }

    public Dictionary<string, IncludeJournalRecord> TakeSnapshot()
    {
        var copy = new Dictionary<string, IncludeJournalRecord>(Records);
        Records.Clear();
        return copy;
    }

    public void MergeBack(Dictionary<string, IncludeJournalRecord> batch)
    {
        foreach (var (id, record) in batch) Records[id] = record;
    }

    public List<EntryOptions> Apply(List<EntryOptions> data)
    {
        var result = data.Select(CloneEntry).ToList();
        foreach (var (id, record) in Records)
        {
            if (record.Kind == IncludeJournalKind.Remove)
            {
                Detach(result, id);
                continue;
            }
            var flat = Flatten(result).GetValueOrDefault(id);
            if (flat is null)
            {
                var options = new EntryOptions { Id = id };
                ApplyChanges(options, record.Changes);
                Place(result, options, record.Relocated ? record.Parent : null, record.Position);
            }
            else
            {
                ApplyChanges(flat.Options, record.Changes);
                if (record.Relocated && flat.Parent != record.Parent)
                {
                    Detach(result, id);
                    Place(result, flat.Options, record.Parent, record.Position);
                }
            }
        }
        return result;
    }

    public List<IncludeConflict> Reconcile(
        Dictionary<string, IncludeFlatEntry> baseMap,
        Dictionary<string, IncludeFlatEntry> theirs,
        Func<string, string?, bool> fileOwned)
    {
        var conflicts = new List<IncludeConflict>();
        foreach (var (id, record) in Records.ToList())
        {
            if (!fileOwned(id, null)) continue;
            var b = baseMap.GetValueOrDefault(id);
            var t = theirs.GetValueOrDefault(id);
            if (record.Kind == IncludeJournalKind.Remove)
            {
                if (t is null)
                {
                    Records.Remove(id);
                }
                else if (b is null || !CordisUtils.DeepEqual(t.Options, b.Options))
                {
                    conflicts.Add(new IncludeConflict(id, b is null ? "added in file, removed at runtime" : "modified in file, removed at runtime"));
                    Records.Remove(id);
                }
                continue;
            }
            if (t is null)
            {
                if (b is not null)
                {
                    conflicts.Add(new IncludeConflict(id, "removed in file, modified at runtime"));
                    Records.Remove(id);
                }
                continue;
            }
            foreach (var key in record.Changes.Keys.ToList())
            {
                if (!fileOwned(id, key)) continue;
                var bv = b?.Options[key];
                var tv = t.Options[key];
                if (CordisUtils.DeepEqual(bv, tv) || CordisUtils.DeepEqual(tv, record.Changes[key])) continue;
                conflicts.Add(new IncludeConflict(id, $"key \"{key}\" modified both in file and at runtime"));
                record.Changes.Remove(key);
            }
            if (record.Relocated && b is not null && t.Parent != b.Parent && t.Parent != record.Parent)
            {
                conflicts.Add(new IncludeConflict(id, "moved both in file and at runtime"));
                record.Relocated = false;
            }
            if (record.Changes.Count == 0 && !record.Relocated) Records.Remove(id);
        }
        return conflicts;
    }

    public static Dictionary<string, object?> Diff(EntryOptions? legacy, EntryOptions options)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var key in (legacy?.Keys ?? []).Concat(options.Keys).Distinct())
        {
            if (key == "id") continue;
            if (!CordisUtils.DeepEqual(legacy?[key], options[key])) changes[key] = options[key];
        }
        return changes;
    }

    public static void ApplyChanges(EntryOptions options, IEnumerable<KeyValuePair<string, object?>> changes)
    {
        foreach (var (key, value) in changes) options[key] = value;
    }

    public static Dictionary<string, IncludeFlatEntry> Flatten(
        IEnumerable<EntryOptions> data,
        string? parent = null,
        Dictionary<string, IncludeFlatEntry>? result = null)
    {
        result ??= new Dictionary<string, IncludeFlatEntry>();
        var list = data.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var options = list[i];
            if (options.Id is not null) result[options.Id] = new IncludeFlatEntry(parent, i, options, list);
            if (options.Group && options.Config is List<object?> children)
            {
                Flatten(children.Select(EntryOptions.From), options.Id, result);
            }
        }
        return result;
    }

    public static bool Detach(List<EntryOptions> data, string id)
    {
        var flat = Flatten(data).GetValueOrDefault(id);
        if (flat is null) return false;
        flat.List.Remove(flat.Options);
        return true;
    }

    public static bool Place(List<EntryOptions> data, EntryOptions options, string? parent, int position)
    {
        if (parent is null)
        {
            data.Insert(Math.Min(position, data.Count), options);
            return true;
        }
        var group = Flatten(data).GetValueOrDefault(parent);
        if (group?.Options.Group != true) return false;
        if (group.Options.Config is not List<object?> list)
        {
            list = [];
            group.Options.Config = list;
        }
        list.Insert(Math.Min(position, list.Count), options);
        return true;
    }

    public static EntryOptions CloneEntry(EntryOptions options)
    {
        var clone = new EntryOptions();
        foreach (var key in options.Keys) clone[key] = CloneValue(options[key]);
        return clone;
    }

    private static object? CloneValue(object? value)
    {
        return value switch
        {
            EntryOptions options => CloneEntry(options),
            Dictionary<string, object?> dict => dict.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value)),
            List<object?> list => list.Select(CloneValue).ToList(),
            _ => value,
        };
    }
}
