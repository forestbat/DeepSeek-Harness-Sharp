using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Llm;

/**
 * 端点推理强度的探测结果(落盘键 = 线路|端点|模型)。
 * OpenAI 兼容网关与 Anthropic 网关的 /models 都没有推理强度字段, 只能拿一次真实请求去问端点;
 * 问出来的答案必须落盘, 否则每次启动、每个进程都要重新试探。清单为空表示端点明确拒绝, 之后不再试。
 * 键里带线路: 同一台网关的 openai 与 anthropic 两条线路档位可能不同, 不能互相复用。
 */
public sealed class ReasoningEffortsCache
{
    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, IReadOnlyList<string>>? _entries;

    public ReasoningEffortsCache(string? path) => _path = string.IsNullOrWhiteSpace(path) ? null : path;

    /** 探测结论的键: 线路 + 端点 + 模型。 */
    public static string Key(string wire, string endpoint, string model) => $"{wire}|{endpoint.TrimEnd('/')}|{model}";

    /** 探测过返回 true(含被端点拒绝的空清单); 从没探测过返回 false, 由调用方决定何时去试。 */
    public bool TryGet(string key, out IReadOnlyList<string> efforts)
    {
        lock (_gate)
        {
            _entries ??= Load();
            if (_entries.TryGetValue(key, out var found))
            {
                efforts = found;
                return true;
            }
            efforts = [];
            return false;
        }
    }

    /** 记录探测结论; 空清单表示端点拒绝该参数。 */
    public void Save(string key, IReadOnlyList<string> efforts)
    {
        lock (_gate)
        {
            _entries ??= Load();
            _entries[key] = efforts;
            if (_path is { } path)
                Write(path, _entries);
        }
    }

    private Dictionary<string, IReadOnlyList<string>> Load()
    {
        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (_path is not { } path || !File.Exists(path))
            return entries;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return entries;
            foreach (var (key, value) in root)
            {
                if (value is JsonArray array)
                    entries[key] = [.. array.OfType<JsonValue>().Select(id => id.GetValue<string>()).Where(id => id.Length > 0)];
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 缓存损坏就从空开始: 探测结论是可选加速, 不该让端点配置起不来。
            return entries;
        }
        return entries;
    }

    /** 原子写(临时文件 + 改名): 多个进程/窗口同时探测时不能读到半截文件。 */
    private static void Write(string path, Dictionary<string, IReadOnlyList<string>> entries)
    {
        var root = new JsonObject();
        foreach (var (key, efforts) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            root[key] = new JsonArray([.. efforts.Select(id => (JsonNode)JsonValue.Create(id))]);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 写不进去只影响下次启动重新探测, 不影响本次结果(内存里已经有)。
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
