using System.Security.Cryptography;
using System.Text.Json;

namespace Dsh.Boot;

/** /plugins add 的落盘:把目标程序集与 deps.json 声明的托管依赖闭包拷进 plugins/<程序集名>/。
 *  同名文件内容一致则跳过,不一致则拒绝——插件目录是重启后的唯一发现入口,内容分歧必须显式。 */
public static class PluginInstall
{
    public static string Install(string sourcePath, string pluginsRoot)
    {
        var staging = Path.Combine(pluginsRoot, Path.GetFileNameWithoutExtension(sourcePath));
        Directory.CreateDirectory(staging);
        var hostImage = HostImageNames();
        var files = new List<string> { sourcePath };
        var depsJson = Path.ChangeExtension(sourcePath, ".deps.json");
        if (File.Exists(depsJson))
        {
            files.Add(depsJson);
            files.AddRange(RuntimeDependencies(depsJson, Path.GetDirectoryName(sourcePath)!)
                .Where(file => !hostImage.Contains(Path.GetFileNameWithoutExtension(file))));
        }
        foreach (var file in files.Distinct(StringComparer.Ordinal))
            CopyIfChanged(file, Path.Combine(staging, Path.GetFileName(file)));
        return Path.Combine(staging, Path.GetFileName(sourcePath));
    }

    /** 删除 add 落盘的插件目录;只允许 plugins 根下的每插件子目录,拒绝删根或根外路径。 */
    public static void Uninstall(string directory, string pluginsRoot)
    {
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Length == 0
            || string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
            || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒绝删除 plugins 根或根外目录: {target}");
        Directory.Delete(target, recursive: true);
    }

    /** 宿主镜像(可执行文件旁)的程序集不随插件落盘:它们由宿主解析,拷贝只会制造第二份类型身份。 */
    private static HashSet<string> HostImageNames()
        => new(
            Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> RuntimeDependencies(string depsJsonPath, string sourceDir)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(depsJsonPath));
        if (!document.RootElement.TryGetProperty("targets", out var targets))
            yield break;
        foreach (var target in targets.EnumerateObject())
        foreach (var library in target.Value.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out var runtime))
                continue;
            foreach (var file in runtime.EnumerateObject())
            {
                var candidate = Path.Combine(sourceDir, Path.GetFileName(file.Name));
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static void CopyIfChanged(string source, string destination)
    {
        if (File.Exists(destination))
        {
            if (ContentEquals(source, destination))
                return;
            throw new InvalidOperationException($"conflicting file already installed: {destination}");
        }
        File.Copy(source, destination);
    }

    private static bool ContentEquals(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }
}
