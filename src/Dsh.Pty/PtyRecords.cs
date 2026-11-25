using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Pty;

/**
 * 一个 pty 的启动参数与归属, 落在 daemon 的 run 目录里。
 *
 * pty id 是"子进程那一次生命"的句柄, 本来是纯内存的; 落盘之后它才具备"按同一个 id 复活"的前提 ——
 * daemon 重启、或 TUI 自己退出之后, 仍能用同一套参数(工作目录、环境、home)重新拉起,
 * 并把上一次的 agent 会话 id 告诉用户。
 */
public sealed class PtyRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = [];

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("environment")]
    public Dictionary<string, string?>? Environment { get; set; }

    [JsonPropertyName("home")]
    public string? Home { get; set; }

    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    [JsonPropertyName("columns")]
    public int Columns { get; set; }

    [JsonPropertyName("wantsMouse")]
    public bool WantsMouse { get; set; }

    /** 上一次 identify/publish-panes 上报的 agent 会话 id: pty 退出后, 这是继续那条会话的入口。 */
    [JsonPropertyName("agentSessionId")]
    public string? AgentSessionId { get; set; }

    /** 尺寸代次: 跨 daemon 重启保持单调, 常驻 TUI 才不会因为代次回退而漏掉整帧重画。 */
    [JsonPropertyName("sizeGeneration")]
    public long SizeGeneration { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/**
 * 记录文件的序列化上下文: 发布档(裁剪/AOT)默认关掉反射序列化, 走反射的 JsonSerializer 在宿主里会抛异常,
 * 表现为"终端起不来"。源生成的上下文在两种形态下都可用。
 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PtyRecord))]
internal partial class PtyRecordJsonContext : JsonSerializerContext;

public static class PtyRecords
{
    private const string Prefix = "pty-";
    private const string Suffix = ".json";

    /** 参数就是 run 目录本身(默认取 daemon 的默认 run 目录); 记录与尺寸文件都跟着 daemon 的端点走。 */
    public static string DirectoryOf(string? directory = null) => directory ?? PtyDaemonPaths.RunDirectory();

    public static string PathFor(string id, string? directory = null)
        => Path.Combine(DirectoryOf(directory), $"{Prefix}{id}{Suffix}");

    public static void Save(PtyRecord record, string? directory = null)
    {
        try
        {
            record.UpdatedAt = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(DirectoryOf(directory));
            var path = PathFor(record.Id, directory);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, PtyRecordJsonContext.Default.PtyRecord));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static PtyRecord? TryLoad(string id, string? directory = null)
    {
        var path = PathFor(id, directory);
        if (!File.Exists(path))
            return null;
        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllText(path), PtyRecordJsonContext.Default.PtyRecord);
            return record is { FileName.Length: > 0 } ? record : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<PtyRecord> List(string? runDirectory = null)
    {
        var directory = DirectoryOf(runDirectory);
        if (!Directory.Exists(directory))
            return [];
        var records = new List<PtyRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, $"{Prefix}*{Suffix}"))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), PtyRecordJsonContext.Default.PtyRecord);
                if (record is { FileName.Length: > 0 })
                    records.Add(record);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return records;
    }

    public static void Delete(string id, string? directory = null)
    {
        try
        {
            var path = PathFor(id, directory);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
