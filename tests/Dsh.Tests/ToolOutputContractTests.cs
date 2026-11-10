using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 输出契约的守门员: 工具的返回值必须能通过它自己声明的输出 schema。
 *
 * 这套断言存在的理由是一次真实事故 —— subagent 的 background 分支漏声明 runId、
 * 又把可选的 jobId 写成必需, 于是每次后台调用都在**子代理已经启动之后**报"工具调用失败",
 * 调用方遂重试, 同一个任务被跑了两遍。契约漂移只在运行期、副作用之后才暴露,
 * 而且长得像"工具失败", 所以必须在这里静态拦下。
 *
 * 用真实装配(ConfigBoot.Compose)枚举实际注册的工具, 不猜源码写法、不 mock 工具注册。
 */
public sealed class ToolOutputContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        $"contract-{Guid.NewGuid():N}");

    /** 每个已注册工具声明的输出 schema 都必须能被校验器支持: 不支持的写法等于没有契约。 */
    [Fact]
    public async Task EveryRegisteredToolOutputSchema_IsSupportedByTheValidator()
    {
        var failures = new List<string>();
        foreach (var (name, definition) in await RegisteredToolsAsync())
        {
            try
            {
                JsonSchemaValidator.AssertSupported(definition.Output.Schema);
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /**
     * 闭合分支(`additionalProperties: false`)最容易出事故: 分支 `required` 的字段在 `properties` 里
     * 没有声明, 或者一个分支把"本来可选"的字段写成必需。前者这里直接拦; 后者靠每个工具自己补一条
     * "真实返回值能过 schema"的用例(见 SubagentTests.OutputContract_*)。
     */
    [Fact]
    public async Task RequiredFields_AreAlwaysDeclaredInProperties()
    {
        var failures = new List<string>();
        foreach (var (name, definition) in await RegisteredToolsAsync())
        {
            foreach (var branch in Branches(definition.Output.Schema))
            {
                if (branch["properties"] is not { } properties || branch["required"] is not { } required)
                    continue;
                foreach (var item in required.AsArray())
                {
                    var field = item?.GetValue<string>();
                    if (field is not null && properties[field] is null)
                        failures.Add($"{name}: required \"{field}\" 没有在 properties 里声明");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /**
     * 事故回归: subagent 的 background 分支曾漏声明 `runId`, 还把可选的 `jobId` 写成必需,
     * 于是真实返回值在 two 个点上同时违规、oneOf 零匹配, 而校验发生在子代理启动之后。
     * 这条用例把那份坏 schema 固化为"必须被判违规", 保证校验器与巡检都还认得出这类形态。
     */
    [Fact]
    public void TheSubagentBackgroundDefect_IsStillDetected()
    {
        var broken = new System.Text.Json.Nodes.JsonObject
        {
            ["oneOf"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new System.Text.Json.Nodes.JsonArray("kind", "jobId"),
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                    ["runId"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                },
            }),
        };
        var value = System.Text.Json.JsonDocument.Parse("""{"kind":"background","runId":"run-1"}""").RootElement;

        var violations = JsonSchemaValidator.Validate(broken, value, "value");

        Assert.Contains(violations, violation => violation.Contains("oneOf", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("jobId", StringComparison.Ordinal));
    }

    /** 巡检必须真的覆盖到工具目录, 否则上面两条断言会静默地什么也没检查。 */
    [Fact]
    public async Task ContractSweep_SeesTheToolCatalog()
    {
        var tools = await RegisteredToolsAsync();
        Assert.True(tools.Count >= 10, $"只扫到 {tools.Count} 个工具, 契约巡检实际上没覆盖到工具目录");
        Assert.Contains(tools, tool => tool.Name == "subagent");
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonObject> Branches(System.Text.Json.Nodes.JsonObject schema)
    {
        foreach (var key in (string[])["oneOf", "anyOf"])
        {
            if (schema[key] is not { } alternatives)
                continue;
            foreach (var alternative in alternatives.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
                yield return alternative;
            yield break;
        }
        yield return schema;
    }

    private async Task<IReadOnlyList<(string Name, ToolDefinition Definition)>> RegisteredToolsAsync()
    {
        Directory.CreateDirectory(_directory);
        var home = HarnessHome.Resolve(Path.Combine(_directory, "home"));
        using var app = await ConfigBoot.Compose(
            new HarnessOptions(home, Cwd: _directory, IsTui: false),
            global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("the composition has no tool runtime");
        return [.. tools.All().Select(definition => (definition.Name, definition))];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
