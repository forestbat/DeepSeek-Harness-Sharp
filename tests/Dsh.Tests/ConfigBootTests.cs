using System.Text.Json;
using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Tests;

public class ConfigBootTests
{
    private const string PluginRepo = "/tmp/kilo/dsh-anchored-standard/eternal-minimal";

    [Fact]
    public async Task Compose_ActivatesPluginsAndGateway()
    {
        if (!Directory.Exists(PluginRepo)) return;
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            foreach (var file in new[] { "eternal-minimal.mjs", "instruction-hint.mjs", "compaction-epoch.mjs" })
                File.Copy(Path.Combine(PluginRepo, file), Path.Combine(dir, file));
            await File.WriteAllTextAsync(Path.Combine(dir, "test.cordis.yml"), """
                - id: eternal-minimal
                  name: ./eternal-minimal.mjs

                - id: persona
                  name: '@deepseek-ai/dsh-persona'
                  config:
                    text: You are a helpful software engineer assistant.
                    complete: true
                    includeRuntimeContext: false

                - id: instruction-hint
                  name: ./instruction-hint.mjs
                  config:
                    promoteOn: assistant-message

                - id: tool-bash
                  name: '@deepseek-ai/dsh-tool-bash'
                  disabled: !!js process.platform === 'win32'

                - id: tool-fs
                  name: '@deepseek-ai/dsh-tool-fs'

                - id: tool-fs-search
                  name: '@deepseek-ai/dsh-tool-fs-search'
                  config:
                    sampleOverCapGlobResults: false

                - id: tool-todo
                  name: '@deepseek-ai/dsh-tool-todo'
                  config:
                    allowParallelInProgress: true

                # fs-local 留在 isolate 组内以验证 group/realm 通路;instruction-hint 解析的是 root fs,不受影响。
                - id: bootstrap-filesystem
                  name: cordis:group
                  group: true
                  isolate:
                    fs: true
                  config:
                    - id: fs-local
                      name: '@deepseek-ai/dsh-fs-local'
                      config:
                        cwd: !!js process.env.DSH_CWD ?? process.cwd()

                    - id: str-replace-editor
                      name: '@deepseek-ai/dsh-tool-str-replace-editor'
                      config:
                        maxOutputChars: 16000
                """);

            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            // 激活失败会在 Compose 内 fail-loud 抛出,正常返回即代表所有启用条目已激活。
            app = await ConfigBoot.Compose(Path.Combine(dir, "test.cordis.yml"), new HarnessOptions(home, Cwd: dir));

            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            foreach (var name in new[] { "bash", "read", "write", "edit", "glob", "grep", "todo_write", "str_replace_editor" })
                Assert.NotNull(tools.Get(name));

            Assert.NotNull(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName));

            // instruction-hint 的条目已激活(JS 插件以导出 name 登记 runtime)。
            Assert.Contains(app.Ctx.Registry.Values(), runtime =>
                runtime.Name == "instruction-hint" && runtime.Fibers.All(fiber => fiber.State == FiberState.Active));

            // 条目并行激活,注册顺序不稳定;排序后喂给过滤器以保证断言确定。
            var assembly = new Dictionary<string, object?>
            {
                ["system"] = "base prompt",
                ["tools"] = tools.Schemas().OrderBy(schema => schema.Name, StringComparer.Ordinal).Select(schema => (object?)new Dictionary<string, object?>
                {
                    ["name"] = schema.Name,
                    ["description"] = schema.Description,
                }).ToList(),
            };
            var result = await app.Ctx.Events.Waterfall(null, SystemPrompt.AssembleEvent,
                [assembly, new Dictionary<string, object?>()],
                () => new ValueTask<object?>(assembly));
            var filtered = Assert.IsType<Dictionary<string, object?>>(result);
            var names = Assert.IsType<List<object?>>(filtered["tools"])
                .Select(tool => Assert.IsType<Dictionary<string, object?>>(tool)["name"] as string)
                .ToList();
            Assert.Equal(["bash", "str_replace_editor"], names);

            // deny 通道:网关把 dshx list 的结果物化成 bash 调用的错误结果,载荷即目录清单。
            var gatewayResult = await tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("test-gateway-list"),
                Name = "bash",
                Arguments = JsonDocument.Parse("""{"command":"dshx list","description":"List gateway tools"}""").RootElement,
                Signal = default,
            });
            Assert.True(gatewayResult.IsError);
            var text = Assert.IsType<TextBlock>(gatewayResult.Content[0]).Text;
            Assert.Contains("dshx gateway", text);
            Assert.Contains("todo_write", text);
        }
        finally
        {
            app?.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
