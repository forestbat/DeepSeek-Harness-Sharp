using System.Text;
using Cordis;
using Cordis.Node;

namespace Dsh.Tools;

public sealed record LocalFsConfig
{
    public string? Cwd { get; init; }
}

public sealed class LocalFsService : Service
{
    public const string ServiceName = "fs";

    public LocalFsService(Context ctx, LocalFsConfig? config = null) : base(ctx, ServiceName)
    {
        Cwd = Path.GetFullPath(config?.Cwd ?? Environment.CurrentDirectory);
    }

    public string Cwd { get; }

    // 服务实例的 provide 生命周期挂在调用方 fiber 上,此处无额外资源需要释放。
    public static IDisposable Register(Context ctx, string? cwd = null)
    {
        _ = new LocalFsService(ctx, new LocalFsConfig { Cwd = cwd });
        return new CompositeDisposable();
    }

    // cordis Node 桥按名字大小写敏感地解析成员,以下方法供 JS 插件以 camelCase 调用。
    public object resolve(string path, IDictionary<string, object?>? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("fs.resolve: path must be a non-empty string");
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        var baseDir = options is not null && options.TryGetValue("cwd", out var cwd) ? cwd as string : null;
        return Path.GetFullPath(string.IsNullOrEmpty(baseDir) ? Path.Combine(Cwd, path) : Path.Combine(baseDir, path));
    }

    public object stat(string path, object? signal = null)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            return new Dictionary<string, object?>
            {
                ["type"] = "file",
                ["isFile"] = true,
                ["isDirectory"] = false,
                ["size"] = info.Length,
                ["mtimeMs"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            };
        }
        if (Directory.Exists(full))
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "directory",
                ["isFile"] = false,
                ["isDirectory"] = true,
            };
        }
        return JsUndefined.Instance;
    }

    public object readText(string path, object? signal = null)
        => File.ReadAllText(Path.GetFullPath(path), new UTF8Encoding(false, false));

    public object writeText(string path, string content, object? signal = null)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        File.WriteAllText(full, content, new UTF8Encoding(false, false));
        return new Dictionary<string, object?> { ["path"] = full };
    }

    public object exists(string path, object? signal = null)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) || Directory.Exists(full);
    }
}
