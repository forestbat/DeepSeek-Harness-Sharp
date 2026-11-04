namespace Dsh.GitInspect;

public static class GitRevisionFiles
{
    public static async Task<string> RootAsync(string directory, CancellationToken cancellationToken = default)
        => Path.GetFullPath((await GitCommand.RunCheckedAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false)).Trim());

    public static async Task<IReadOnlyList<GitFileStat>> ChangesAsync(string directory, string revision, string? gitDirectory = null, CancellationToken cancellationToken = default)
    {
        var prefix = Prefix(directory, gitDirectory);
        var verify = await GitCommand.RunAsync(directory, [.. prefix, "rev-parse", "--verify", $"{revision}^{{commit}}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var hasRevision = verify.ExitCode == 0;
        if (!hasRevision && revision != "HEAD")
            throw new GitCommandException("rev-parse", verify.ExitCode, verify.Error);
        var changes = hasRevision
            ? GitDiffEntryParser.Parse(await GitCommand.RunCheckedAsync(directory, [.. prefix, "diff", revision, "--raw", "--numstat", "-z", "-M", "--no-ext-diff", "--no-textconv", "--"], cancellationToken).ConfigureAwait(false))
            : [];
        var untrackedArguments = hasRevision ? new[] { "--others", "--exclude-standard", "-z" } : ["--cached", "--others", "--exclude-standard", "-z"];
        var untracked = await GitCommand.RunCheckedAsync(directory, [.. prefix, "ls-files", .. untrackedArguments], cancellationToken).ConfigureAwait(false);
        return [.. changes, .. untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => new GitFileStat(path, null, GitChangeKind.Added, null, null))];
    }

    public static Task CopyToAsync(string directory, string revision, string path, Stream destination, string? gitDirectory = null, CancellationToken cancellationToken = default)
        => GitCommand.CopyOutputAsync(directory, [.. Prefix(directory, gitDirectory), "show", $"{revision}:{path}"], destination, cancellationToken);

    private static string[] Prefix(string directory, string? gitDirectory)
        => gitDirectory is null ? [] : [$"--git-dir={gitDirectory}", $"--work-tree={directory}"];
}
