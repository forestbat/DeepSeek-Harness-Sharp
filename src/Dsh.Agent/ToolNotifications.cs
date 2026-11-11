using Dsh.Runtime.Events;
using Dsh.Core;

namespace Dsh.Agent;

public sealed record ToolPreExecuteNotification(ToolRunContext Run) : INotification
{
    public static string EventName => "tools/pre-execute";
}

public sealed record ToolExecuteNotification(ToolRunContext Run) : INotification
{
    public static string EventName => "tools/execute";
}

public sealed record ToolPostExecuteNotification(ToolRunContext Run, ToolExecutionResult Result) : INotification
{
    public static string EventName => "tools/post-execute";
}

public sealed record ToolResultNotification(ToolRunContext Run, ToolExecutionResult Result) : INotification
{
    public static string EventName => "tools/result";
}

public sealed record ToolChangeNotification : INotification
{
    public static string EventName => "tools/change";
}
