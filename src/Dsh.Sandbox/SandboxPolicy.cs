using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

internal sealed record SandboxPath(string Source, string Target, bool IsDirectory)
{
    public SafeFileHandle? Pin { get; set; }
}

internal sealed record SandboxPolicy(
    string Executable, string[] Arguments, string WorkingDirectory, bool GuestOnlyWorkingDirectory,
    SandboxPath[] Readable, SandboxPath[] Writable, string[] Denied,
    bool AllowNetwork, bool AllowChildProcesses, long MemoryLimitBytes, string[] EnvironmentEntries) : IDisposable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private const int MaximumPaths = 4096;
    private const int MaximumArgumentBytes = 128 * 1024;
    private readonly List<SafeFileHandle> _pins = [];
    internal Dictionary<string, SafeFileHandle> DeniedPins { get; } = new(StringComparer.Ordinal);
    internal List<SandboxPath> ProtectedAliases { get; } = [];

    public static SandboxPolicy Create(SandboxProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MemoryLimitBytes, 1);
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
        if (OperatingSystem.IsLinux())
            protectedRuntime = protectedRuntime.Append(LinuxSandbox.LauncherExecutablePath);
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
        var policy = new SandboxPolicy(executable.Source, arguments, working, options.GuestOnlyWorkingDirectory, readable, writable, denied, options.AllowNetwork, options.AllowChildProcesses, options.MemoryLimitBytes, []);
        try
        {
            if (!OperatingSystem.IsWindows())
                policy.PinUnixPaths();
            ValidateGrants(readable, writable, denied);
            if (!OperatingSystem.IsWindows())
                policy.ProtectUnixAliases();
            return policy with { EnvironmentEntries = SandboxEnvironment.Create(options.Environment, writable) };
        }
        catch { policy.Dispose(); throw; }
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
        foreach (var deny in denied)
        {
            if (OperatingSystem.IsWindows() && File.Exists(deny) && NativeFileIdentity.Get(deny).Links != 1)
                throw new NotSupportedException("Denied files must have exactly one hard link; aliases cannot be protected by pathname alone.");
            if (writable.Any(write => Contains(write.Source, deny)) && !File.Exists(deny))
                throw new NotSupportedException("A denied target inside a writable directory must be an existing single-link regular file.");
        }
    }

    private void PinUnixPaths()
    {
        foreach (var grant in Writable.Concat(Readable).OrderBy(static grant => grant.Source.Length))
        {
            grant.Pin = PinPath(grant.Source, grant.IsDirectory);
            if (!string.Equals(UnixPathPin.PathOf(grant.Pin), grant.Source, StringComparison.Ordinal))
                throw new IOException("A sandbox grant changed location while its authority was being validated.");
        }
        foreach (var path in Denied.Where(File.Exists))
        {
            var pin = PinPath(path, directory: false);
            var identity = NativeFileIdentity.Get(pin);
            if (!identity.IsRegular || identity.Links != 1)
                throw new NotSupportedException("Denied files must be single-link regular files; aliases cannot be protected by pathname alone.");
            DeniedPins.Add(path, pin);
        }
    }

    internal SafeFileHandle PinPath(string path, bool directory)
    {
        var ancestor = Writable.Concat(Readable).Where(grant => grant.Pin is not null && SandboxPolicy.Contains(grant.Source, path))
            .OrderByDescending(static grant => grant.Source.Length).FirstOrDefault();
        if (ancestor is not null && ancestor.Source == path)
            return ancestor.Pin!;
        SafeFileHandle pin;
        if (ancestor is null)
            pin = UnixPathPin.Open(path, directory);
        else
        {
            var components = Path.GetRelativePath(ancestor.Source, path).Split('/');
            var parent = ancestor.Pin!;
            for (var index = 0; index < components.Length; index++)
            {
                pin = UnixPathPin.OpenRelative(parent, components[index], index != components.Length - 1 || directory);
                _pins.Add(pin);
                parent = pin;
            }
            return parent;
        }
        _pins.Add(pin);
        return pin;
    }

    private void ProtectUnixAliases()
    {
        const int maximumWritableEntries = 32768;
        const int maximumProtectedEntries = 262144;
        var candidates = new Dictionary<(ulong Device, ulong Inode), List<SandboxPath>>();
        var writableCount = 0;
        void CountWritableEntry()
        {
            if (++writableCount > maximumWritableEntries)
                throw new NotSupportedException("Writable trees exceed the 32768-entry identity validation bound; use narrower grants.");
        }
        foreach (var write in Writable)
            Visit(write, (path, pin, directory) =>
            {
                if (directory)
                    return;
                var identity = NativeFileIdentity.Get(pin);
                if (!identity.IsRegular || (!OperatingSystem.IsMacOS() && identity.Links <= 1))
                    return;
                var key = (identity.Device, identity.Inode);
                if (!candidates.TryGetValue(key, out var aliases))
                    candidates.Add(key, aliases = []);
                aliases.Add(new SandboxPath(path, path, false));
            }, CountWritableEntry);
        if (candidates.Count == 0)
            return;
        var protectedCount = 0;
        void CountProtectedEntry()
        {
            if (++protectedCount > maximumProtectedEntries)
                throw new NotSupportedException("Read-only trees exceed the 262144-entry identity validation bound; use narrower grants.");
        }
        var protectedIdentities = new HashSet<(ulong Device, ulong Inode)>();
        foreach (var pin in DeniedPins.Values)
        {
            var identity = NativeFileIdentity.Get(pin);
            var key = (identity.Device, identity.Inode);
            if (candidates.ContainsKey(key))
                protectedIdentities.Add(key);
        }
        var visitedReadDirectories = new HashSet<(ulong Device, ulong Inode)>();
        foreach (var read in Readable)
            Visit(read, (_, pin, directory) =>
            {
                if (!directory)
                {
                    var identity = NativeFileIdentity.Get(pin);
                    var key = (identity.Device, identity.Inode);
                    if (candidates.ContainsKey(key))
                        protectedIdentities.Add(key);
                }
            }, CountProtectedEntry, visitedReadDirectories);
        foreach (var key in protectedIdentities)
            foreach (var alias in candidates[key].DistinctBy(static alias => alias.Source, StringComparer.Ordinal))
            {
                alias.Pin = PinPath(alias.Source, directory: false);
                var identity = NativeFileIdentity.Get(alias.Pin);
                if ((identity.Device, identity.Inode) != key)
                    throw new IOException("A writable alias changed identity during sandbox validation.");
                ProtectedAliases.Add(alias);
            }
    }

    private static void Visit(SandboxPath root, Action<string, SafeFileHandle, bool> visit, Action countEntry, HashSet<(ulong Device, ulong Inode)>? visitedDirectories = null)
    {
        void Walk(string path, SafeFileHandle pin, bool directory, int depth)
        {
            countEntry();
            if (depth > 128)
                throw new NotSupportedException("A sandbox tree exceeds the 128-directory traversal depth bound; use narrower grants.");
            if (directory && visitedDirectories is not null)
            {
                var identity = NativeFileIdentity.Get(pin);
                if (!visitedDirectories.Add((identity.Device, identity.Inode)))
                    return;
            }
            visit(path, pin, directory);
            if (!directory)
                return;
            foreach (var entry in UnixPathPin.Entries(pin))
            {
                if (!entry.Directory && !entry.Regular)
                {
                    countEntry();
                    continue;
                }
                using var child = UnixPathPin.OpenRelative(pin, entry.Name, entry.Directory);
                Walk(Path.Combine(path, entry.Name), child, entry.Directory, depth + 1);
            }
        }
        Walk(root.Source, root.Pin!, root.IsDirectory, 0);
    }

    public void Dispose()
    {
        foreach (var pin in _pins)
            pin.Dispose();
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
    {
        if (!OperatingSystem.IsWindows())
        {
            var resolved = realpath(path, 0);
            if (resolved == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot canonicalize a sandbox path.");
            try { return Marshal.PtrToStringUTF8(resolved)!; }
            finally { free(resolved); }
        }
        var root = Path.GetPathRoot(path)!;
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', root.Length) >= 0)
            throw new NotSupportedException("Sandbox grants require local paths without alternate data streams.");
        var current = root;
        foreach (var component in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("Windows sandbox grants cannot traverse reparse points.");
        }
        using var handle = WindowsNative.CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open a canonical sandbox path.");
        var buffer = new StringBuilder(512);
        var length = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity && length < 32768)
        {
            buffer.Capacity = checked((int)length + 1);
            length = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot normalize a Windows sandbox path.");
        var normalized = buffer.ToString();
        if (!normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) || normalized.Length < 7 || normalized[5] != ':')
            throw new NotSupportedException("A Windows sandbox grant did not resolve to a local drive path.");
        return Path.TrimEndingDirectorySeparator(normalized[4..]);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint buffer);
    [DllImport("libc")]
    private static extern void free(nint pointer);
}
