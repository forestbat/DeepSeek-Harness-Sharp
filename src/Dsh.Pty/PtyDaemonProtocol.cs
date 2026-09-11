using System.Text.Json;

namespace Dsh.Pty;

public sealed class PtyDaemonNotRunningException : Exception
{
    public PtyDaemonNotRunningException()
        : base("daemon not running")
    {
    }
}

public static class PtyDaemonPaths
{
    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    public static string SocketPath(string? root = null)
        => Path.Combine(root ?? DefaultRoot(), "run", "dsh.sock");

    public static string PortFile(string? root = null)
        => Path.Combine(root ?? DefaultRoot(), "run", "dsh.port");

    public static string RunDirectory(string? root = null)
        => Path.GetDirectoryName(SocketPath(root))!;
}

public sealed class PtyDaemonRequest
{
    public string Method { get; set; } = "";

    public string? Id { get; set; }

    public PtyDaemonStartParams? Params { get; set; }
}

public sealed class PtyDaemonStartParams
{
    public string? Id { get; set; }

    public string FileName { get; set; } = "";

    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string?>? Environment { get; set; }

    public int Rows { get; set; } = 24;

    public int Columns { get; set; } = 80;
}

public sealed class PtyDaemonSessionDto
{
    public string Id { get; set; } = "";

    public string Command { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public int? Pid { get; set; }

    public string Status { get; set; } = "";

    public int? ExitCode { get; set; }

    public bool IsAttached { get; set; }
}

public sealed class PtyDaemonResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<PtyDaemonSessionDto>? Sessions { get; set; }

    public PtyDaemonSessionDto? Session { get; set; }
}

internal static class PtyDaemonJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}