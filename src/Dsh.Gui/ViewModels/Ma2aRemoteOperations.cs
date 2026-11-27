using Dsh.Ma2a.Services;

namespace Dsh.Gui.ViewModels;

public sealed record Ma2aRemoteOperation(Ma2aServiceOperations.Operation Definition, IReadOnlyList<Ma2aFieldViewModel> Fields)
{
    public string Family => Definition.Family;
    public string Name => Definition.Name;
    public string Label => $"{Family} / {Name}";
    public bool IsMutation => Definition.IsMutation;

    public Ma2aServiceOperations.PreparedForm Prepare()
        => Definition.PrepareForm(Fields.ToDictionary(field => field.Name, field => field.BuildValue(), StringComparer.Ordinal));

    public static IReadOnlyList<Ma2aRemoteOperation> Create()
        => Ma2aServiceOperations.All.Select(operation =>
            new Ma2aRemoteOperation(operation, operation.FormFields.Select(CreateField).ToArray())).ToArray();

    private static Ma2aFieldViewModel CreateField(Ma2aServiceOperations.FormField definition)
    {
        var field = new Ma2aFieldViewModel(definition.Name, definition.Schema);
        if (definition.InitialNumber is { } value)
            field.Number = value;
        return field;
    }
}
