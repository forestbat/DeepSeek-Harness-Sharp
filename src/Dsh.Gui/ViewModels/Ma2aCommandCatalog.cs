using System.Text.Json;

namespace Dsh.Gui.ViewModels;

/** Native operator forms are derived from the pinned MA2A contract, including nested typed values. */
public static class Ma2aCommandCatalog
{
    private static readonly JsonElement Contract = Dsh.Ma2a.Ma2aLocalContract.Contract;

    public static IReadOnlyList<Ma2aCommandDefinition> Commands { get; } = Contract.GetProperty("commands")
        .EnumerateObject().Where(command => command.Name is not ("snapshot_fetch" or "stream_open" or "service_attach" or "service_accept"))
        .Select(command => new Ma2aCommandDefinition(command.Name, command.Value)).ToArray();

    public static JsonElement Resolve(JsonElement schema) => schema.TryGetProperty("ref", out var reference)
        ? Resolve(Contract.GetProperty("types").GetProperty(reference.GetString()!)) : schema;
}

public sealed record Ma2aCommandDefinition(string Operation, JsonElement Schema)
{
    public string Group => Operation switch
    {
        _ when Operation.StartsWith("space_", StringComparison.Ordinal) => "Spaces / 授权",
        _ when Operation.StartsWith("agent_", StringComparison.Ordinal) => "Agents / 任务",
        _ when Operation.StartsWith("process_", StringComparison.Ordinal) => "持久进程",
        _ when Operation.StartsWith("service_", StringComparison.Ordinal) => "资源暴露 / 托管服务",
        _ when Operation.Contains("relay", StringComparison.Ordinal) => "连接 / 中继",
        _ when Operation.StartsWith("ui_", StringComparison.Ordinal) || Operation == "session_revoke_all" => "Web 控制台",
        _ => "Runtime / Endpoint",
    };

    public string Label => Operation.Replace('_', ' ');
    public bool IsMutation => Schema.GetProperty("fields").TryGetProperty("request_id", out _);
    public bool RequiresConfirmation => IsMutation || Operation is "ui_start" or "ui_stop";
    public IReadOnlyList<Ma2aFieldViewModel> CreateFields() => Schema.GetProperty("fields").EnumerateObject()
        .Where(field => field.Name != "request_id")
        .Select(field => new Ma2aFieldViewModel(field.Name, field.Value)).ToArray();
}
