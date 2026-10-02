using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 插件模板工程随解决方案构建; 本测试装载其产物并激活, 锁住模板注释所描述的行为(清单生成、包名、Inject、提示词段)。 */
public sealed class PluginTemplateTests
{
    [Fact]
    public async Task TemplatePlugin_LoadsAndActivates()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md")))
            root = root.Parent;
        var pluginPath = Path.Combine(
            root?.FullName ?? AppContext.BaseDirectory,
            "templates", "managed-plugin", "bin", "Debug", "net10.0", "plugin", "DshPluginTemplate.dll");
        if (!File.Exists(pluginPath))
            Assert.Skip($"模板插件尚未构建: {pluginPath}");

        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("@example/dsh-plugin-template", loaded.Packages);

        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var definition = host.Catalog.CreateDefinition("@example/dsh-plugin-template");
        var activation = await ctx.Scheduler.AddAsync(definition);

        Assert.Equal(ActivationState.Active, activation.State);
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var assembly = await prompt.Assemble(new AssembleContext());
        Assert.Contains(assembly.Sections, section => section.Name == "template:hello");
    }
}
