using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace Dsh.Boot;

/** Selects bounded, header-validated bundled code, not a mathematically minimal static-import closure. */
internal static class PluginSandboxNativeCode
{
    private const int MaximumBundleFiles = 256;
    private const int MaximumHeaderEntries = 4096;

    internal static void Add(string artifact, HashSet<string> paths)
    {
        var full = Path.GetFullPath(artifact);
        ValidateAndAdd(full, paths);
        var count = 1;
        foreach (var sibling in Directory.EnumerateFiles(Path.GetDirectoryName(full)!))
        {
            if (PluginSandboxLaunch.PathComparer.Equals(full, sibling) || !IsLibrary(sibling))
                continue;
            if (++count > MaximumBundleFiles)
                throw new InvalidDataException("The bundled native code namespace exceeds its bounded capacity.");
            ValidateAndAdd(sibling, paths);
        }
    }

    internal static void AddImport(string source, string name, string? resolved, HashSet<string> paths)
    {
        var directory = Path.GetDirectoryName(source)!;
        if (resolved is null)
        {
            var direct = Path.GetFullPath(name, directory);
            if (File.Exists(direct))
                resolved = direct;
            else if (!Path.IsPathRooted(name))
            {
                var file = OperatingSystem.IsWindows() ? name + ".dll"
                    : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
                var candidate = Path.GetFullPath(file, directory);
                if (File.Exists(candidate))
                    resolved = candidate;
            }
        }
        if (resolved is not null)
        {
            resolved = Path.GetFullPath(resolved);
            var relative = Path.GetRelativePath(directory, resolved);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
                Add(resolved, paths);
            else
                ValidateAndAdd(resolved, paths);
        }
    }

    private static bool IsLibrary(string path)
        => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(".so.", StringComparison.Ordinal)
            || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);

    private static void ValidateAndAdd(string path, HashSet<string> paths)
    {
        path = Path.GetFullPath(path);
        if (paths.Contains(path))
            return;
        PluginSandboxLaunch.RejectLink(path);
        using var stream = File.OpenRead(path);
        if (!HasCodeHeader(stream, 0, stream.Length))
            throw new InvalidDataException("The bundled native dependency is not a supported executable code image.");
        paths.Add(path);
    }

    private static bool HasCodeHeader(FileStream stream, long start, long length)
    {
        if (length < 32)
            return false;
        Span<byte> header = stackalloc byte[32];
        stream.Position = start;
        stream.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if ((magic >> 16) == 0x4d5a && start == 0)
        {
            stream.Position = 0;
            using var image = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return image.PEHeaders.PEHeader is { SizeOfImage: > 0 }
                && image.PEHeaders.SectionHeaders.Any(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0);
        }
        if (magic == 0x7f454c46)
        {
            if (header[4] is not (1 or 2) || header[5] is not (1 or 2))
                return false;
            var little = header[5] == 1;
            var type = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) : BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
            var wide = header[4] == 2;
            if (type is not (2 or 3) || length < (wide ? 64 : 52))
                return false;
            Span<byte> elf = stackalloc byte[64];
            stream.Position = start;
            stream.ReadExactly(elf[..(wide ? 64 : 52)]);
            var table = wide ? Read64(elf[32..], little) : Read32(elf[28..], little);
            var stride = Read16(elf[(wide ? 54 : 42)..], little);
            var count = Read16(elf[(wide ? 56 : 44)..], little);
            return count is > 0 and <= MaximumHeaderEntries && stride >= (wide ? 56 : 32)
                && table <= (ulong)length && (ulong)stride * count <= (ulong)length - table;
        }
        if (magic is 0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe)
        {
            var little = magic is 0xcefaedfe or 0xcffaedfe;
            var wide = magic is 0xfeedfacf or 0xcffaedfe;
            var type = Read32(header[12..], little);
            var count = Read32(header[16..], little);
            var bytes = Read32(header[20..], little);
            return type is 2 or 6 or 8 && count is > 0 and <= MaximumHeaderEntries
                && bytes >= count * 8 && bytes <= 16 * 1024 * 1024 && bytes <= length - (wide ? 32 : 28);
        }
        if (magic is 0xcafebabe or 0xcafebabf or 0xbebafeca or 0xbfbafeca)
        {
            var little = magic is 0xbebafeca or 0xbfbafeca;
            var wide = magic is 0xcafebabf or 0xbfbafeca;
            var count = Read32(header[4..], little);
            if (count is 0 or > 16 || 8 + count * (wide ? 32 : 20) > length)
                return false;
            Span<byte> sliceMagic = stackalloc byte[4];
            for (uint index = 0; index < count; index++)
            {
                stream.Position = start + 8 + index * (wide ? 32 : 20);
                stream.ReadExactly(header[..(wide ? 32 : 20)]);
                var offset = wide ? Read64(header[8..], little) : Read32(header[8..], little);
                var size = wide ? Read64(header[16..], little) : Read32(header[12..], little);
                if (offset > (ulong)length || size > (ulong)length - offset || size < 32)
                    return false;
                // Universal slices must be thin images; nested fat headers are not recursively admitted.
                stream.Position = checked(start + (long)offset);
                stream.ReadExactly(sliceMagic);
                var value = BinaryPrimitives.ReadUInt32BigEndian(sliceMagic);
                if (value is not (0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe)
                    || !HasCodeHeader(stream, checked(start + (long)offset), checked((long)size)))
                    return false;
            }
            return true;
        }
        return false;
    }

    private static ushort Read16(ReadOnlySpan<byte> bytes, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint Read32(ReadOnlySpan<byte> bytes, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    private static ulong Read64(ReadOnlySpan<byte> bytes, bool little)
        => little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
}
