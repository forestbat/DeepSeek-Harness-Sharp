using System.Text.Json;
using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.Tests;

public class ConfigBootTests
{
    [Fact]
    public async Task Compose_ActivatesCSharpPlugins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "test.cordis.yml"), """
                - id: persona
                  name: '@deepseek-ai/dsh-persona'
                  config:
                    text: You are a helpful software engineer assistant.
                    complete: true
                    includeRuntimeContext: false

                - id: tool-bash
                  name: '@deepseek-ai/dsh-tool-bash'

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

                - id: bootstrap-filesystem
                  name: cordis:group
                  group: true
                  isolate:
                    fs: true
                  config:
                    - id: fs-local
                      name: '@deepseek-ai/dsh-fs-local'

                    - id: str-replace-editor
                      name: '@deepseek-ai/dsh-tool-str-replace-editor'
                      config:
                        maxOutputChars: 16000
                """);

            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            app = await ConfigBoot.Compose(Path.Combine(dir, "test.cordis.yml"), new HarnessOptions(home, Cwd: dir));

            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            foreach (var name in new[] { "bash", "read", "write", "edit", "glob", "grep", "todo_write", "str_replace_editor" })
                Assert.NotNull(tools.Get(name));

            Assert.NotNull(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName));

            var assembly = new Dictionary<string, object?>
            {
                ["system"] = "base prompt",
                ["tools"] = tools.Schemas()
                    .OrderBy(schema => schema.Name, StringComparer.Ordinal)
                    .Select(schema => (object?)new Dictionary<string, object?>
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
            Assert.Contains("bash", names);
            Assert.Contains("str_replace_editor", names);
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
