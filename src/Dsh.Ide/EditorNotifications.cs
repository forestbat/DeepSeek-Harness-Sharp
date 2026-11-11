using Dsh.Runtime.Events;
using Dsh.Core;

namespace Dsh.Ide;

public sealed record EditorSourceLocation(string Path, int Line = 1, int Column = 1);

public sealed record EditorTestFailureNotification(IAgent Agent, int Turn, string Test, IReadOnlyList<EditorSourceLocation> Locations) : INotification
{
    public static string EventName => "test/failure";
}
