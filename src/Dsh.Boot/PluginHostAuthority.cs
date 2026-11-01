using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Boot;

/** A package-name claim cannot grant an untrusted artifact authority inside the host process. */
internal sealed class PluginHostAuthority : IDisposable, Dsh.Core.IRuntimeCodeIntegrity
{
    internal const string ServiceName = "pluginHostAuthority";
    internal const string ImageArtifact = "image";
    private const string DigestPrefix = "sha256:";
    private const int MaximumArtifactFiles = 256;
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int BufferBytes = 64 * 1024;
    private readonly Context _context;
    private readonly string _cacheRoot;
    private readonly Lock _sync = new();
    private readonly HashSet<string> _protectedPaths;
    private readonly HashSet<(ulong Device, ulong Inode)> _protectedIdentities = [];
    private readonly List<string> _snapshots = [];
    private bool _runtimeIndexed;

    internal PluginHostAuthority(Context context, HarnessHome home)
    {
        _context = context;
        _cacheRoot = Dsh.Transport.PinnedFileAccess.Canonicalize(Path.Combine(home.Root, "runtime", "host-plugin-code"));
        OwnerOnlyFiles.EnsureDirectory(_cacheRoot);
        _protectedPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            _cacheRoot,
            Dsh.Transport.PinnedFileAccess.Canonicalize(AppContext.BaseDirectory),
        };
        context.Provide(ServiceName, this);
        context.Provide(Dsh.Core.IRuntimeCodeIntegrity.ServiceName, this);
        IndexProtectedPath(_cacheRoot);
        IndexProtectedPath(AppContext.BaseDirectory);
    }

    internal static bool IsArtifactToken(string value)
        => value == ImageArtifact || value.StartsWith(DigestPrefix, StringComparison.Ordinal)
            && value.Length == DigestPrefix.Length + SHA256.HashSizeInBytes * 2
            && value.AsSpan(DigestPrefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    internal static bool Grants(PluginSetting setting, string artifact)
        => setting.Execution == PluginExecution.Host
            && string.Equals(setting.HostArtifact, artifact, StringComparison.OrdinalIgnoreCase)
            && (setting.Permissions ?? PluginPermissions.AllAllowed) == PluginPermissions.AllAllowed;

    internal static Task<string> IdentifyAsync(string path, CancellationToken signal = default)
        => ProcessArtifactAsync(path, destination: null, signal);

    internal async Task<VerifiedPluginBundle> SnapshotAsync(string source, string expectedArtifact, CancellationToken signal = default)
    {
        OwnerOnlyFiles.EnsureDirectory(_cacheRoot);
        var directory = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        OwnerOnlyFiles.EnsureDirectory(directory);
        try
        {
            var digests = new Dictionary<string, string>(PluginSandboxLaunch.PathComparer);
            var actual = await ProcessArtifactAsync(source, directory, signal, digests).ConfigureAwait(false);
            if (!string.Equals(actual, expectedArtifact, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeException("PLUGIN_HOST_ARTIFACT_CHANGED", "The plugin deployment changed after its explicit host-code grant.");
            var bundle = new VerifiedPluginBundle(Path.Combine(directory, Path.GetFileName(source)), digests);
            lock (_sync)
            {
                _snapshots.Add(directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                    IndexProtectedPath(file);
            }
            return bundle;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    /** Read-only intersections protect code without granting additional directory reads. */
    internal IReadOnlyList<string> ReadOnlyPaths(IReadOnlyList<string> writablePaths)
    {
        var writable = writablePaths.Select(path => Dsh.Transport.PinnedFileAccess.Canonicalize(path)).ToArray();
        lock (_sync)
        {
            IndexRuntime();
            var overlays = new HashSet<string>(PluginSandboxLaunch.PathComparer);
            foreach (var path in _protectedPaths)
                if (File.Exists(path) || Directory.Exists(path))
                    foreach (var directory in writable)
                        if (ContainsPath(directory, path))
                            overlays.Add(path);
                        else if (ContainsPath(path, directory) && Directory.Exists(directory))
                            overlays.Add(directory);
            return overlays.ToArray();
        }
    }

    internal void ProtectCode(IEnumerable<string> paths)
    {
        lock (_sync)
            foreach (var path in paths)
            {
                var canonical = Dsh.Transport.PinnedFileAccess.Canonicalize(path);
                if (File.Exists(canonical) || Directory.Exists(canonical))
                    IndexProtectedPath(canonical);
            }
    }

    public void DemandWritable(PinnedFileTarget target)
    {
        var canonical = target.Path;
        lock (_sync)
        {
            IndexRuntime();
            foreach (var protectedPath in _protectedPaths)
                if (ContainsPath(protectedPath, canonical))
                    throw CodeMutationDenied();
            if (target.Identity is { } identity && _protectedIdentities.Contains((identity.Device, identity.Inode))
                || _protectedIdentities.Contains((target.ParentIdentity.Device, target.ParentIdentity.Inode)))
                throw CodeMutationDenied();
        }
    }

    private static RuntimeException CodeMutationDenied()
        => new("PLUGIN_HOST_CODE_WRITE_DENIED", "Sandbox-protected writes cannot mutate host runtime or registered plugin code.");

    private static bool ContainsPath(string directory, string path)
        => path.Length >= directory.Length
            && path.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && (path.Length == directory.Length || Path.EndsInDirectorySeparator(directory)
                || path[directory.Length] == Path.DirectorySeparatorChar);

    private void IndexRuntime()
    {
        if (_runtimeIndexed)
            return;
        foreach (var path in SandboxRuntimePaths.ForCurrentHost())
        {
            var canonical = Dsh.Transport.PinnedFileAccess.Canonicalize(path);
            _protectedPaths.Add(canonical);
            if (File.Exists(canonical) || Directory.Exists(canonical))
                IndexProtectedPath(canonical);
        }
        _runtimeIndexed = true;
    }

    private void IndexProtectedPath(string path)
    {
        _protectedPaths.Add(Dsh.Transport.PinnedFileAccess.Canonicalize(path));
        var identity = NativeFileIdentity.Get(path);
        _protectedIdentities.Add((identity.Device, identity.Inode));
    }

    private static IReadOnlyList<string> ArtifactFiles(string source)
    {
        var manifest = Path.ChangeExtension(source, ".deps.json");
        if (File.Exists(manifest) && new FileInfo(manifest).Length > MaximumManifestBytes)
            throw new RuntimeException("PLUGIN_DEPLOYMENT_TOO_LARGE", "The plugin dependency manifest exceeds the bounded inspection limit.");
        var form = PluginSandboxLaunch.InspectForm(source);
        var runtime = new HashSet<string>(SandboxRuntimePaths.ForCurrentHost(), PluginSandboxLaunch.PathComparer);
        var closure = PluginSandboxLaunch.CodeClosure(
            [new PluginSandboxSource(Dsh.Plugins.PluginDescriptor.For("host-artifact", form), source)]);
        var files = PluginInstall.ArtifactFiles(source).Concat(closure.Where(path => !runtime.Contains(path) && File.Exists(path)))
            .Select(Path.GetFullPath).Distinct(PluginSandboxLaunch.PathComparer).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (files.Select(Path.GetFileName).Distinct(PluginSandboxLaunch.PathComparer).Count() != files.Length)
            throw new RuntimeException("PLUGIN_DEPLOYMENT_COLLISION", "The frozen plugin code bundle contains conflicting file names.");
        if (files.Length > MaximumArtifactFiles || files.Sum(file => new FileInfo(file).Length) > MaximumArtifactBytes)
            throw new RuntimeException("PLUGIN_DEPLOYMENT_TOO_LARGE", "The plugin deployment exceeds the bounded host-code inspection limit.");
        return files;
    }

    private static async Task<string> ProcessArtifactAsync(string source, string? destination, CancellationToken signal,
        Dictionary<string, string>? digests = null)
    {
        var files = ArtifactFiles(source);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        long processed = 0;
        try
        {
            foreach (var file in files)
            {
                signal.ThrowIfCancellationRequested();
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = input.Length;
                AppendHeader(hash, Path.GetFileName(file), length);
                await using var output = destination is null ? null : OwnerOnlyFiles.Create(Path.Combine(destination, Path.GetFileName(file)));
                using var fileHash = digests is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var total = await HashFileAsync(input, output, hash, buffer, MaximumArtifactBytes - processed, signal, fileHash).ConfigureAwait(false);
                processed = checked(processed + total);
                if (total != length || input.Length != length)
                    throw new IOException("The plugin deployment changed while its code identity was being read.");
                if (digests is not null)
                    digests.Add(Path.Combine(destination!, Path.GetFileName(file)), Convert.ToHexStringLower(fileHash!.GetHashAndReset()));
            }
            return $"{DigestPrefix}{Convert.ToHexStringLower(hash.GetHashAndReset())}";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<long> HashFileAsync(FileStream input, FileStream? output, IncrementalHash hash,
        byte[] buffer, long remaining, CancellationToken signal, IncrementalHash? fileHash)
    {
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), signal).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > remaining)
                throw new RuntimeException("PLUGIN_DEPLOYMENT_TOO_LARGE", "The plugin deployment grew beyond its bounded code inspection limit.");
            hash.AppendData(buffer, 0, read);
            fileHash?.AppendData(buffer, 0, read);
            if (output is not null)
                await output.WriteAsync(buffer.AsMemory(0, read), signal).ConfigureAwait(false);
        }
        if (output is not null)
            await output.FlushAsync(signal).ConfigureAwait(false);
        return total;
    }

    private static void AppendHeader(IncrementalHash hash, string name, long length)
    {
        Span<byte> header = stackalloc byte[sizeof(long)];
        var nameBytes = Encoding.UTF8.GetByteCount(name);
        Span<byte> encodedName = stackalloc byte[nameBytes];
        Encoding.UTF8.GetBytes(name, encodedName);
        BinaryPrimitives.WriteInt32LittleEndian(header, nameBytes);
        hash.AppendData(header[..sizeof(int)]);
        hash.AppendData(encodedName);
        BinaryPrimitives.WriteInt64LittleEndian(header, length);
        hash.AppendData(header);
    }

    public void Dispose()
    {
        foreach (var directory in _snapshots)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                _context.LoggerFor("plugins").Warn("Trusted plugin code is still mapped; its private snapshot could not yet be removed.");
            }
            catch (UnauthorizedAccessException)
            {
                _context.LoggerFor("plugins").Warn("Trusted plugin code snapshot cleanup was denied.");
            }
        }
        _snapshots.Clear();
    }
}
