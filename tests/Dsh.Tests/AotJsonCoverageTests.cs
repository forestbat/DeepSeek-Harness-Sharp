using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using Dsh.Jobs;
using Dsh.PtyTerminal;
using Dsh.Tools;
using Dsh.Workflow;

namespace Dsh.Tests;

/** 工具返回值在 AOT 下没有反射回退:每个结果类型都必须被某个源生成上下文覆盖。
 *  这里只查上下文能否提供元数据(与运行时反射解析器无关),覆盖缺失即失败。 */
public sealed class AotJsonCoverageTests
{
    public static TheoryData<Type> ResultTypes =>
    [
        typeof(TodoWriteResult),
        typeof(TodoCounts),
        typeof(JobOutputResult),
        typeof(JobKillResult),
        typeof(PublicJobSnapshot),
        typeof(List<PublicJobSnapshot>),
        typeof(TerminalSendBackgroundResult),
        typeof(TerminalSendForegroundResult),
        typeof(TerminalCloseResult),
        typeof(TerminalReadResult),
        typeof(TerminalSignalResult),
        typeof(TerminalSpawnResult),
        typeof(TerminalSessionStatus),
        typeof(TerminalSessionSnapshot),
        typeof(List<TerminalSessionSnapshot>),
        typeof(IReadOnlyList<TerminalSessionSnapshot>),
        typeof(RalphRunResult),
        typeof(WorkflowRunToolResult),
        typeof(Dictionary<string, object?>),
        typeof(IReadOnlyDictionary<string, object?>),
        typeof(List<object?>),
        typeof(IReadOnlyList<object?>),
        typeof(Dsh.Sdk.InitializeParams),
        typeof(Dsh.Sdk.InitializeResult),
        typeof(Dsh.Sdk.SessionPromptParams),
        typeof(Dsh.Sdk.SessionEventNotification),
        typeof(Dsh.Sdk.ServiceCallParams),
    ];

    [Theory]
    [MemberData(nameof(ResultTypes))]
    public void ToolResultType_HasSourceGeneratedMetadata(Type type)
        => Assert.True(CoveredByContext(type),
            $"{type.FullName} 没有源生成 JSON 元数据;AOT 下工具返回值会因缺少 JsonTypeInfo 而失败");

    private static readonly ConcurrentDictionary<Assembly, IReadOnlyList<JsonSerializerContext>> Contexts = new();

    /** DSH 程序集里的源生成上下文;BCL/集合类型(如 Dictionary)的元数据由某个 DSH 上下文提供。 */
    private static bool CoveredByContext(Type type)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.GetName().Name?.StartsWith("Dsh.", StringComparison.Ordinal) ?? true)
                continue;
            foreach (var context in Contexts.GetOrAdd(assembly, FindContexts))
            {
                if (context.GetTypeInfo(type) is not null)
                    return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<JsonSerializerContext> FindContexts(Assembly assembly)
    {
        var contexts = new List<JsonSerializerContext>();
        foreach (var candidate in Types(assembly))
        {
            if (candidate.IsAbstract || !typeof(JsonSerializerContext).IsAssignableFrom(candidate))
                continue;
            if (candidate.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) is JsonSerializerContext context)
            {
                contexts.Add(context);
            }
        }
        return contexts;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.Where(type => type is not null)!;
        }
    }
}
