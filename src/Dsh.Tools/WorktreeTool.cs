using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tools;

public sealed record CreatedWorktreeValue(string Path, string Branch, string BaseCommit, string RepositoryRoot);

/** create_worktree: a new branch and linked checkout under <checkout root>/.agents/worktrees, entered through the session working directory. */
public static class WorktreeTool
{
    public const string ToolName = "create_worktree";

    private const string DefaultRevision = "HEAD";
    private const string GeneratedNamePrefix = "worktree-";
    private const int GeneratedNameHexChars = 8;
    private const string PoolIgnoreContent = "*\n";
    private const int GitOutputBytes = 64 * 1024;

    private const string Description =
        "Create a new Git branch and worktree from a local commit, branch, or tag, then change this session's working directory to it. "
        + "The checkout goes to <current checkout root>/.agents/worktrees/<name>. "
        + "Defaults to HEAD and a generated name; no fetch is performed. "
        + "Uncommitted files stay in the source checkout. Existing branch names or checkout paths fail. "
        + "Use working_directory with cd to leave; the checkout and branch remain.";

    private const string SectionText =
        "Use create_worktree to start isolated work on a new branch without touching the current checkout; the session then works inside the new worktree.";

    private static readonly IReadOnlyDictionary<string, string?> GitEnvironment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    private static readonly System.Text.Json.Nodes.JsonObject OutputSchema = ToolSchemas.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["path", "branch", "baseCommit", "repositoryRoot"],
          "properties": {
            "path": { "type": "string" },
            "branch": { "type": "string" },
            "baseCommit": { "type": "string" },
            "repositoryRoot": { "type": "string" }
          }
        }
        """);

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var subprocess = ctx.Get<SubprocessService>(SubprocessService.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal($"tool:{ToolName}", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = Description,
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["name"] = ToolSchemas.StringParam("New branch name, also used as the checkout directory. Omit to generate a unique name."),
                    ["from"] = ToolSchemas.StringParam("Local commit, branch, or tag to start from. Defaults to HEAD."),
                }),
            Output = new ToolOutputDefinition(OutputSchema, (_, value) => [new TextBlock(Render(value))]),
            Execute = (args, exec) => Execute(args, exec, subprocess),
            WriteTargets = (_, exec) => CheckoutRoot(exec.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory) is { } repository
                ? [PoolOf(repository), Path.Combine(repository, ".git")]
                : [],
        });
        return new CompositeDisposable(section, registration);
    }

    private static string Render(JsonElement value)
    {
        var created = DshJson.Deserialize<CreatedWorktreeValue>(value)
                      ?? throw new JsonException("create_worktree result value is malformed");
        return $"Created worktree {created.Path} on new branch {created.Branch} at {created.BaseCommit} (repository {created.RepositoryRoot}). The working directory is now {created.Path}.";
    }

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec, SubprocessService subprocess)
    {
        var session = exec.Agent?.Session ?? throw new InvalidOperationException("create_worktree requires an agent session");
        var repository = CheckoutRoot(session.WorkingDirectory ?? Environment.CurrentDirectory)
                         ?? throw new InvalidOperationException("create_worktree: the working directory is not inside a Git checkout");
        var name = Argument(args, "name") ?? $"{GeneratedNamePrefix}{Guid.NewGuid().ToString("N")[..GeneratedNameHexChars]}";
        var from = Argument(args, "from") ?? DefaultRevision;
        if (name.StartsWith('-') || from.StartsWith('-'))
            throw new ArgumentException("create_worktree: name and from must not start with '-'");
        await Git(subprocess, repository, ["check-ref-format", "--branch", name], exec.Signal);
        var baseCommit = await Git(subprocess, repository, ["rev-parse", "--verify", $"{from}^{{commit}}"], exec.Signal);
        var checkout = await AllocateCheckout(repository, name, exec.Signal);
        await Git(subprocess, repository, ["worktree", "add", "-b", name, checkout, baseCommit], exec.Signal);
        return new CreatedWorktreeValue(SessionWorkingDirectory.Change(session, checkout), name, baseCommit, repository);
    }

    private static string? Argument(JsonElement args, string name)
        => args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value
            ? value
            : null;

    /** The nearest directory holding a .git entry: a main checkout has a .git directory, a linked worktree a .git file. */
    private static string? CheckoutRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return Path.TrimEndingDirectorySeparator(directory.FullName);
        }
        return null;
    }

    private static string PoolOf(string repository) => Path.Combine(repository, ".agents", "worktrees");

    /** Reserves an unused checkout path; the pool ignores its own content only when this call creates it. */
    private static async Task<string> AllocateCheckout(string repository, string name, CancellationToken signal)
    {
        var pool = PoolOf(repository);
        var checkout = Path.GetFullPath(Path.Combine(pool, name));
        if (Directory.Exists(checkout) || File.Exists(checkout))
            throw new InvalidOperationException($"create_worktree: checkout path already exists: {checkout}");
        if (Directory.Exists(pool))
            return checkout;
        Directory.CreateDirectory(pool);
        await File.WriteAllTextAsync(Path.Combine(pool, ".gitignore"), PoolIgnoreContent, signal);
        return checkout;
    }

    private static async Task<string> Git(SubprocessService subprocess, string repository, IReadOnlyList<string> arguments, CancellationToken signal)
    {
        using var handle = await subprocess.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = ["git", .. arguments],
            Cwd = repository,
            Env = GitEnvironment,
            ModelExecution = true,
            Stdout = new SubprocessCollect(GitOutputBytes, null),
            Stderr = new SubprocessCollect(GitOutputBytes, null),
            Signal = signal,
        });
        var outcome = await handle.Done;
        signal.ThrowIfCancellationRequested();
        if (outcome.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed: {handle.StderrReader.ReadFrom(0).Text.Trim()}");
        return handle.StdoutReader.ReadFrom(0).Text.Trim();
    }
}
