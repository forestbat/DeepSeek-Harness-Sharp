using Dsh.Runtime.Events;

namespace Dsh.Core;

public sealed record EditorSourceLocation(string Path, int Line = 1, int Column = 1);

public sealed record EditorTestFailureNotification(IAgent Agent, int Turn, string Test, IReadOnlyList<EditorSourceLocation> Locations) : INotification
{
    public static string EventName => "test/failure";
}
