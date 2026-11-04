using System.Text;

namespace Dsh.Sandbox;

internal sealed record SandboxPath(string Source, string Target, bool IsDirectory);

internal sealed record SandboxPolicy(
    string Executable, string[] Arguments, string WorkingDirectory, bool GuestOnlyWorkingDirectory,
    SandboxPath[] Readable, SandboxPath[] Writable, string[] Denied,
    bool AllowNetwork, bool AllowChildProcesses, string[] EnvironmentEntries)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const int MaximumPaths = 4096;
    private const int MaximumArgumentBytes = 128 * 1024;

    public static SandboxPolicy Create(SandboxProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RuntimePaths.Count + options.ReadOnlyPaths.Count + options.WritablePaths.Count + options.DeniedWritePaths.Count > MaximumPaths)
            throw new ArgumentException("The sandbox path grant limit is 4096.", nameof(options));
        if (options.RuntimePaths.Concat(options.ReadOnlyPaths).Concat(options.WritablePaths).Concat(options.DeniedWritePaths).Sum(static value => (long)Encoding.UTF8.GetByteCount(ValidateText(value)) + 1) > 1024 * 1024)
            throw new ArgumentException("Sandbox path grants exceed the 1 MiB UTF-8 limit.", nameof(options));
        if (options.Arguments.Count > 4096 || options.Arguments.Sum(static value => (long)Encoding.UTF8.GetByteCount(ValidateText(value)) + 1) > MaximumArgumentBytes)
            throw new ArgumentException("The sandbox argument limit is 128 KiB of UTF-8 and 4096 arguments.", nameof(options));
        var arguments = options.Arguments.Select(ValidateText).ToArray();
        var executable = ExistingPath(options.Executable);
        if (executable.IsDirectory)
            throw new ArgumentException("The sandbox executable must be a file.", nameof(options));
        if (OperatingSystem.IsMacOS() && (File.GetUnixFileMode(executable.Source) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0)
            throw new NotSupportedException("A macOS sandbox executable cannot change credentials through set-user/group-ID execution.");
        var protectedRuntime = options.RuntimePaths.Concat(options.ReadOnlyPaths).Append(executable.Source);
        if (SandboxedProcess.BackendExecutablePath is { } backend)
            protectedRuntime = protectedRuntime.Append(backend);
        var readable = protectedRuntime.Select(ExistingPath).Distinct().ToArray();
        var writable = options.WritablePaths.Select(ExistingPath).Distinct().ToArray();
        var denied = options.DeniedWritePaths.Select(CanonicalDeniedPath).Distinct(StringComparerFromPlatform()).ToArray();
        if (options.GuestOnlyWorkingDirectory && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This backend cannot provide an empty guest-only working directory without exposing the host directory.");
        var working = options.WorkingDirectory is null ? Path.GetPathRoot(executable.Source)!
            : options.GuestOnlyWorkingDirectory ? Path.GetFullPath(ValidateText(options.WorkingDirectory)) : ExistingPath(options.WorkingDirectory).Source;
        if (options.GuestOnlyWorkingDirectory)
        {
            if (working == "/" || working == "/tmp" || new[] { "/proc", "/dev", "/sys", "/nonexistent" }.Any(path => Contains(path, working) || Contains(working, path)))
                throw new NotSupportedException("A guest-only working directory cannot replace kernel or private scratch mounts.");
            if (readable.Concat(writable).Any(grant => Contains(grant.Source, working) || Contains(grant.Target, working)))
                throw new NotSupportedException("A guest-only working directory cannot be covered by a host path grant.");
        }
        else if (!Directory.Exists(working))
            throw new ArgumentException("The sandbox working directory must exist.", nameof(options));
        ValidateGrants(readable, writable, denied);
        return new SandboxPolicy(executable.Source, arguments, working, options.GuestOnlyWorkingDirectory,
            readable, writable, denied, options.AllowNetwork, options.AllowChildProcesses,
            SandboxEnvironment.Create(options.Environment, writable));
    }

    private static void ValidateGrants(SandboxPath[] readable, SandboxPath[] writable, string[] denied)
    {
        foreach (var path in readable.Concat(writable))
        {
            if (path.Source == Path.GetPathRoot(path.Source) || SandboxHostPaths.ExposesHome(path.Source))
                throw new ArgumentException("A sandbox grant must not expose a filesystem root or the user profile root.");
            if (SandboxHostPaths.IsCredentialPath(path.Source))
                throw new ArgumentException("Credential directories cannot be exposed to a sandbox.");
        }
        foreach (var path in writable)
        {
            if (readable.Any(read => Contains(read.Source, path.Source)))
                throw new ArgumentException("A writable grant cannot be inside a read-only grant.");
            if (denied.Any(deny => Contains(deny, path.Source)))
                throw new ArgumentException("A writable sandbox grant is inside a denied path.");
        }
    }


    internal static bool Contains(string parent, string child) => string.Equals(parent, child, PathComparison)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparer StringComparerFromPlatform() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string ValidateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
            throw new ArgumentException("Sandbox paths and arguments cannot contain NUL.");
        return value;
    }

    private static SandboxPath ExistingPath(string value)
    {
        var target = Path.GetFullPath(ValidateText(value));
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new FileNotFoundException("A sandbox grant must already exist; create it in the trusted host before launch.");
        var source = CanonicalExistingPath(target);
        return new SandboxPath(source, target, Directory.Exists(source));
    }

    private static string CanonicalDeniedPath(string value)
    {
        var path = Path.GetFullPath(ValidateText(value));
        var suffix = new Stack<string>();
        while (!File.Exists(path) && !Directory.Exists(path))
        {
            suffix.Push(Path.GetFileName(path));
            path = Path.GetDirectoryName(path) ?? throw new ArgumentException("The denied path has no existing ancestor.");
        }
        path = CanonicalExistingPath(path);
        while (suffix.TryPop(out var component))
            path = Path.Combine(path, component);
        return path;
    }

    internal static string CanonicalExistingPath(string path)
        => global::Dsh.Transport.FilePathAccess.Canonicalize(path, allowNetwork: false);
}
