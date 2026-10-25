namespace Dsh.GitInspect;

/** Parser for `git status --porcelain=v2 -z --branch` (https://git-scm.com/docs/git-status#_porcelain_format_version_2). */
internal static class GitStatusParser
{
    private const char Unchanged = '.';
    private const string InitialOid = "(initial)";
    private const string DetachedHead = "(detached)";
    private const int OrdinaryFieldCount = 9;
    private const int RenameFieldCount = 10;
    private const int UnmergedFieldCount = 11;

    public static GitStatus Parse(string output)
    {
        var state = new StatusAccumulator();
        var records = output.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length == 0)
                continue;
            switch (record[0])
            {
                case '#':
                    state.ApplyHeader(record);
                    break;
                case '1':
                    state.AddTracked(record.Split(' ', OrdinaryFieldCount), originalPath: null);
                    break;
                case '2':
                    state.AddTracked(record.Split(' ', RenameFieldCount), originalPath: records[++index]);
                    break;
                case 'u':
                    state.Conflicted.Add(new GitFileChange(record.Split(' ', UnmergedFieldCount)[^1], GitChangeKind.Unmerged));
                    break;
                case '?':
                    state.Untracked.Add(record[2..]);
                    break;
            }
        }
        return state.Build();
    }

    internal static GitChangeKind KindOf(char code) => code switch
    {
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Unmerged,
        _ => GitChangeKind.Modified,
    };

    private sealed class StatusAccumulator
    {
        private string? _head;
        private string? _oid;
        private string? _upstream;
        private int? _ahead;
        private int? _behind;

        public List<GitFileChange> Staged { get; } = [];
        public List<GitFileChange> Unstaged { get; } = [];
        public List<string> Untracked { get; } = [];
        public List<GitFileChange> Conflicted { get; } = [];

        public void ApplyHeader(string record)
        {
            var parts = record.Split(' ', 3);
            if (parts.Length < 3)
                return;
            switch (parts[1])
            {
                case "branch.oid":
                    _oid = parts[2] == InitialOid ? null : parts[2];
                    break;
                case "branch.head":
                    _head = parts[2] == DetachedHead ? null : parts[2];
                    break;
                case "branch.upstream":
                    _upstream = parts[2];
                    break;
                case "branch.ab":
                    var counts = parts[2].Split(' ');
                    _ahead = int.Parse(counts[0].AsSpan(1));
                    _behind = int.Parse(counts[1].AsSpan(1));
                    break;
            }
        }

        /** XY: X is the index (staged) side, Y the worktree (unstaged) side; '.' means unchanged on that side. */
        public void AddTracked(string[] fields, string? originalPath)
        {
            var (indexCode, worktreeCode) = (fields[1][0], fields[1][1]);
            var path = fields[^1];
            if (indexCode != Unchanged)
                Staged.Add(Change(path, indexCode, originalPath));
            if (worktreeCode != Unchanged)
                Unstaged.Add(Change(path, worktreeCode, originalPath));
        }

        public GitStatus Build()
            => new(new GitBranchStatus(_head, _oid, _upstream, _ahead, _behind), Staged, Unstaged, Untracked, Conflicted);

        private static GitFileChange Change(string path, char code, string? originalPath)
        {
            var kind = KindOf(code);
            return new GitFileChange(path, kind, kind is GitChangeKind.Renamed or GitChangeKind.Copied ? originalPath : null);
        }
    }
}
