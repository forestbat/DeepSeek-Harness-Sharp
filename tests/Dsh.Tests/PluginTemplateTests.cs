using System.Text;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Plugins.Sandbox;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tests;

[Collection(SettingsTemplateTests.RegistryCollection)]
public sealed class PluginTemplateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Configured template prompt.")]
    public async Task TemplatePlugin_OriginalLifecycleBindsConfigurationAndReleasesEffects(string? message)
    {
        var pluginPath = TemplatePath();
        var directory = TempTree.CreateDirectory("template-capabilities");
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        try
        {
            Assert.Contains("@example/dsh-plugin-template", loaded.Packages);
            var ctx = CreateContext(directory, out var tools);
            var definition = host.Catalog.CreateDefinition("@example/dsh-plugin-template");
            var configuration = message is null ? null : new Dictionary<string, object?> { ["message"] = message };
            var activation = await ctx.Scheduler.AddAsync(definition, configuration);
            try
            {
                Assert.Equal(ActivationState.Active, activation.State);
                Assert.NotNull(tools.Get("template_read_file"));
                Assert.NotNull(tools.Get("template_write_file"));
                Assert.NotNull(tools.Get("template_http"));
                var prompt = await ctx.Get<SystemPrompt>()!.Assemble(new AssembleContext());
                Assert.Equal(message ?? "This deployment includes the example template plugin.",
                    Assert.Single(prompt.Sections, section => section.Name == "template:hello").Text);
            }
            finally
            {
                await activation.DeactivateAsync();
            }
            Assert.Null(tools.Get("template_read_file"));
            Assert.Null(tools.Get("template_write_file"));
            Assert.Null(tools.Get("template_http"));
            var unloadedPrompt = await ctx.Get<SystemPrompt>()!.Assemble(new AssembleContext());
            Assert.DoesNotContain(unloadedPrompt.Sections, section => section.Name == "template:hello");
        }
        finally
        {
            loaded.Context?.Unload();
            TempTree.Delete(directory);
        }
        Assert.Null(SettingsTemplate.Verify(SettingsTemplate.DefaultPath));
    }

    [Fact]
    public async Task TemplatePlugin_FileToolsUseRealCommonHostOperations()
    {
        var pluginPath = TemplatePath();
        var directory = TempTree.CreateDirectory("template-file-operations");
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        try
        {
            var ctx = CreateContext(directory, out var tools);
            var definition = host.Catalog.CreateDefinition("@example/dsh-plugin-template");
            var activation = await ctx.Scheduler.AddAsync(definition);
            try
            {
                Assert.Equal(ActivationState.Active, activation.State);
                var path = Path.Combine(directory, "payload.txt");
                var data = Convert.ToBase64String(Encoding.UTF8.GetBytes("capability payload"));
                var write = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                    Name = "template_write_file",
                    Arguments = JsonSerializer.SerializeToElement(new { path, data }),
                    Agent = null,
                    Signal = TestContext.Current.CancellationToken,
                });
                Assert.False(write.IsError);
                Assert.Equal("capability payload", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

                var read = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                    Name = "template_read_file",
                    Arguments = JsonSerializer.SerializeToElement(new { path }),
                    Agent = null,
                    Signal = TestContext.Current.CancellationToken,
                });
                Assert.False(read.IsError);
                Assert.Contains(data, string.Concat(read.Content.OfType<TextBlock>().Select(block => block.Text)));
            }
            finally
            {
                await activation.DeactivateAsync();
            }
        }
        finally
        {
            loaded.Context?.Unload();
            TempTree.Delete(directory);
        }
    }

    [Fact]
    public async Task TemplatePlugin_HttpToolUsesRealHostNetworkOperation()
    {
        var pluginPath = TemplatePath();
        var directory = TempTree.CreateDirectory("template-http-operation");
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        try
        {
            await using var server = await PluginSdkHttpServer.StartAsync();
            var ctx = CreateContext(directory, out var tools);
            var definition = host.Catalog.CreateDefinition("@example/dsh-plugin-template");
            var activation = await ctx.Scheduler.AddAsync(definition);
            try
            {
                Assert.Equal(ActivationState.Active, activation.State);
                var body = Encoding.UTF8.GetBytes("managed request");
                var response = await tools.Execute(new ToolExecutionInput
                {
                    CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                    Name = "template_http",
                    Arguments = JsonSerializer.SerializeToElement(new
                    {
                        url = server.Url,
                        method = "POST",
                        headers = new Dictionary<string, string> { ["X-Plugin"] = "sdk" },
                        body = Convert.ToBase64String(body),
                    }),
                    Agent = null,
                    Signal = TestContext.Current.CancellationToken,
                });
                Assert.False(response.IsError);
                using var document = JsonDocument.Parse(string.Concat(response.Content.OfType<TextBlock>().Select(block => block.Text)));
                Assert.Equal(201, document.RootElement.GetProperty("status").GetInt32());
                Assert.Equal(server.ResponseBody, Convert.FromBase64String(document.RootElement.GetProperty("body").GetString()!));
                Assert.Equal(body, server.RequestBody);
                Assert.Equal("sdk", server.RequestHeader);
                Assert.Equal(1, server.Requests);
            }
            finally
            {
                await activation.DeactivateAsync();
            }
        }
        finally
        {
            loaded.Context?.Unload();
            TempTree.Delete(directory);
        }
    }

    private static Context CreateContext(string directory, out ToolRuntime tools)
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        tools = new ToolRuntime(ctx);
        var settings = new HarnessSettings { Safety = new SafetySettings { AutoApprove = true } };
        _ = new SafetyPolicySource(ctx, () => settings);
        var factory = new PluginCapabilityFactory(ctx, new PluginExecutionEnvironment(new HarnessHome(directory), settings, directory));
        ctx.Provide(IPluginHostFactory.ServiceName, factory);
        return ctx;
    }

    private static string TemplatePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md")))
            root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(root?.FullName ?? AppContext.BaseDirectory,
            "templates", "managed-plugin", "bin", configuration, "net10.0", "plugin", "DshPluginTemplate.dll");
        if (!File.Exists(path))
            Assert.Skip("the managed plugin template has not been built");
        return path;
    }
}
