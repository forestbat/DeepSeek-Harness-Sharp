using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Gui.ViewModels;

public sealed record Ma2aRemoteOperation(string Family, string Name, IReadOnlyList<Ma2aFieldViewModel> Fields,
    Func<IReadOnlyDictionary<string, JsonNode?>, JsonNode>? Build, bool IsMutation, bool IsTerminal)
{
    public string Label => $"{Family} / {Name}";

    public static IReadOnlyList<Ma2aRemoteOperation> Create()
        => Ma2aServiceOperations.All.Select(operation => new Ma2aRemoteOperation(operation.Family, operation.Name,
            operation.FormFields.Select(CreateField).ToArray(), operation.HasRequestBody ? operation.BuildFormRequest : null,
            operation.IsMutation, operation.IsTerminal)).ToArray();

    private static Ma2aFieldViewModel CreateField(Ma2aServiceOperations.FormField definition)
    {
        var field = new Ma2aFieldViewModel(definition.Name, definition.Schema);
        if (definition.InitialNumber is { } value)
            field.Number = value;
        return field;
    }

    public static string S(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<string>() ?? "";
    public static string? O(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<string>();
    public static bool B(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<bool>() == true;
    public static ulong U(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]!.GetValue<ulong>();
}
