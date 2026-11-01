using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Dsh.Plugins;

/** Managed bytes are captured against the digests computed during the granted snapshot copy. */
public sealed class VerifiedPluginBundle
{
    private readonly Dictionary<string, ManagedFile> _assemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths;
    private readonly Dictionary<string, NativeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    public string EntryPath { get; }
    internal ManagedFile Entry => _assemblies.Values.Single(file => file.Path == EntryPath);
    internal IReadOnlyList<string> SharedDependencies => Entry.SharedDependencies;

    public VerifiedPluginBundle(string entryPath, IReadOnlyDictionary<string, string> verifiedFiles)
    {
        if (verifiedFiles.Count > 256 || verifiedFiles.Keys.Sum(path => new FileInfo(path).Length) > 512L * 1024 * 1024)
            throw new InvalidDataException("The verified code bundle exceeds its bounded capacity.");
        EntryPath = System.IO.Path.GetFullPath(entryPath);
        _paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (path, digest) in verifiedFiles)
        {
            var full = System.IO.Path.GetFullPath(path);
            _paths.Add(full);
            if (!_files.TryAdd(System.IO.Path.GetFileName(full), new NativeFile(full, digest)))
                throw new InvalidDataException("The verified code bundle contains conflicting file names.");
            if (!string.Equals(System.IO.Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (source.Length > 512L * 1024 * 1024)
                throw new InvalidDataException("The verified code image exceeds its bounded capacity.");
            var bytes = new byte[checked((int)source.Length)];
            source.ReadExactly(bytes);
            if (source.ReadByte() != -1)
                throw new InvalidDataException("The frozen code image grew before loading.");
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The frozen plugin code changed before loading.");
            using var input = new MemoryStream(bytes, writable: false);
            using var image = new PEReader(input);
            if (!image.HasMetadata)
                continue;
            var metadata = image.GetMetadataReader();
            if (!metadata.IsAssembly)
                continue;
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            var references = metadata.AssemblyReferences.Select(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
            var imports = metadata.MethodDefinitions.Select(handle => metadata.GetMethodDefinition(handle))
                .Where(method => (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                .Select(method => metadata.GetString(metadata.GetModuleReference(method.GetImport().Module).Name))
                .Distinct(StringComparer.Ordinal).ToArray();
            var shared = new List<string>();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference)
                    continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference)
                    continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Namespace) != "Dsh.Plugins"
                    || metadata.GetString(type.Name) != nameof(DshSharedDependencyAttribute))
                    continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1 || value.ReadSerializedString() is not { Length: > 0 } dependency)
                    throw new InvalidDataException("The shared dependency declaration is invalid.");
                shared.Add(dependency);
            }
            if (!_assemblies.TryAdd(name, new ManagedFile(name, full, digest, bytes, references, shared, imports)))
                throw new InvalidDataException("The frozen bundle contains conflicting assembly identities.");
        }
    }

    internal ManagedFile? Find(string name) => _assemblies.GetValueOrDefault(name);

    internal void UseShared(string name, ManagedFile file) => _assemblies[name] = file;

    internal IReadOnlyList<ManagedFile> SharedClosure()
    {
        var closure = new Dictionary<string, ManagedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SharedDependencies)
            Visit(name);
        return closure.Values.ToArray();

        void Visit(string name)
        {
            if (closure.ContainsKey(name))
                return;
            if (Find(name) is not { } file)
            {
                if (PluginLoadContext.ResolveVerifiedHostAssembly(new AssemblyName(name)) is null)
                    throw new FileNotFoundException("A shared dependency is absent from the verified code bundle.");
                return;
            }
            closure.Add(name, file);
            foreach (var reference in file.References)
                Visit(reference);
        }
    }

    internal NativeFile? ResolveNativeFile(string name)
    {
        var leaf = System.IO.Path.GetFileName(name);
        foreach (var candidate in new[] { leaf, leaf + ".dll", "lib" + leaf + ".so", "lib" + leaf + ".dylib" })
            if (_files.TryGetValue(candidate, out var file))
                return file;
        if (System.IO.Path.IsPathRooted(name) || name.IndexOfAny([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]) >= 0)
            throw new FileLoadException("An explicit native dependency path is outside the verified code bundle.");
        return null;
    }

    internal sealed record NativeFile(string Path, string Digest);

    internal string? DemandNativePath(string? path)
    {
        if (path is null)
            return null;
        if (!_paths.Contains(System.IO.Path.GetFullPath(path)))
            throw new FileLoadException("A native dependency is outside the verified code bundle.");
        return path;
    }

    internal sealed class ManagedFile(string name, string path, string digest, byte[] bytes,
        IReadOnlyList<string> references, IReadOnlyList<string> sharedDependencies, IReadOnlyList<string> nativeImports)
    {
        private byte[]? _bytes = bytes;
        internal string Name { get; } = name;
        internal string Path { get; } = path;
        internal string Digest { get; } = digest;
        internal IReadOnlyList<string> References { get; } = references;
        internal IReadOnlyList<string> SharedDependencies { get; } = sharedDependencies;
        internal IReadOnlyList<string> NativeImports { get; } = nativeImports;
        internal Assembly Load(System.Runtime.Loader.AssemblyLoadContext context)
        {
            var image = _bytes ?? throw new InvalidOperationException("The verified assembly bytes have already been consumed.");
            using var stream = new MemoryStream(image, writable: false);
            var assembly = context.LoadFromStream(stream);
            _bytes = null;
            return assembly;
        }
    }
}
