using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Tools;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class ToolPackageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dsh-pkgs-{Guid.NewGuid():N}");
    private readonly HarnessHome _home;

    public ToolPackageTests()
    {
        _home = new HarnessHome(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_home.SettingsFile, """
            logging:
              file: false
            plugins:
              "@deepseek-ai/dsh-checkpoints": false
              "@deepseek-ai/dsh-memory": false
            """);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task CapabilityCatalog_PreservesBaselineContractAndOrder()
    {
        using var input = typeof(ToolPackageTests).Assembly.GetManifestResourceStream("Dsh.Tests.ToolPackageCatalogBaseline.json")!;
        var baseline = JsonNode.Parse(input)!;
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        using var subprocess = new SubprocessService(ctx);
        var host = new PluginHost();
        host.RegisterCompiledIn();
        var entries = new List<PluginEntry>();
        foreach (var package in baseline["packages"]!.AsArray().Select(node => node!.GetValue<string>()))
        {
            Assert.True(host.Catalog.TryCreateDefinition(package, out var definition), package);
            entries.Add(new PluginEntry(definition!, null));
        }
        var composition = await Composition.StartAsync(ctx, entries);
        var expectedOrder = baseline["tools"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).ToList();
        if (OperatingSystem.IsWindows())
        {
            expectedOrder.Insert(expectedOrder.IndexOf("pwsh"), "powershell");
            expectedOrder.Insert(expectedOrder.IndexOf("create_worktree") + 1, "wsl");
        }
        try
        {
            Assert.Equal(expectedOrder, tools.Schemas().Select(schema => schema.Name));
            foreach (var expected in baseline["tools"]!.AsArray())
            {
                var tool = tools.Get(expected!["name"]!.GetValue<string>())!;
                Assert.Equal(expected["description"]!.GetValue<string>().Replace("\r\n", "\n"), tool.Description.Replace("\r\n", "\n"));
                Assert.True(JsonNode.DeepEquals(expected["parameters"], tool.Parameters), tool.Name);
                Assert.True(JsonNode.DeepEquals(expected["output"], tool.Output.Schema), tool.Name);
            }
        }
        finally
        {
            composition.DeactivateAll();
        }
    }

    [Fact]
    public async Task DefaultCatalog_PreservesEveryToolName()
    {
        using var input = typeof(ToolPackageTests).Assembly.GetManifestResourceStream("Dsh.Tests.ToolPackageCatalogBaseline.json")!;
        var baseline = JsonNode.Parse(input)!;
        await File.WriteAllTextAsync(_home.SettingsFile, HarnessSettings.LoadTemplate(), TestContext.Current.CancellationToken);
        using var app = await ConfigBoot.Compose(new HarnessOptions(_home, _directory));
        var expected = baseline["order"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        if (OperatingSystem.IsWindows())
            expected.AddRange(["powershell", "wsl"]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Schemas().Select(schema => schema.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("@deepseek-ai/dsh-tool-fs.files", "read", "glob")]
    [InlineData("@deepseek-ai/dsh-tool-fs.search", "glob", "read")]
    [InlineData("@deepseek-ai/dsh-tool-bash.shell", "bash", "todo_write")]
    [InlineData("@deepseek-ai/dsh-tool-todo.plan", "todo_write", "bash")]
    public async Task NewPackage_DisablesAndReenablesThroughProductLoader(string package, string tool, string retainedTool)
    {
        var settings = HarnessSettings.Load(_home);
        settings.Plugins[package] = new PluginSetting { Enabled = false };
        settings.SavePlugins(_home);
        using var app = await ConfigBoot.Compose(new HarnessOptions(_home, _directory));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        Assert.Null(tools.Get(tool));
        Assert.NotNull(tools.Get(retainedTool));
        var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
        await manager.EnableAsync(package);
        Assert.NotNull(tools.Get(tool));
        Assert.Equal(ActivationState.Active, app.Composition!.Find(package)!.State);
    }

    [Fact]
    public async Task MigratedParameters_ReachFsEditorAndPlan()
    {
        var settings = HarnessSettings.Load(_home);
        settings.Plugins["@deepseek-ai/dsh-fs-local.fs"] = new PluginSetting
        {
            Parameters = new() { ["cwd"] = _directory },
        };
        settings.Plugins["@deepseek-ai/dsh-tool-str-replace-editor.fs"] = new PluginSetting
        {
            Parameters = new() { ["maxOutputChars"] = 4L, ["description"] = "Configured editor contract" },
        };
        settings.Plugins["@deepseek-ai/dsh-tool-todo.plan"] = new PluginSetting
        {
            Parameters = new() { ["allowParallelInProgress"] = false },
            Permissions = new PluginPermissions { Write = false },
        };
        settings.SavePlugins(_home);
        using var app = await ConfigBoot.Compose(new HarnessOptions(_home, _directory));
        Assert.Equal(_directory, app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName)!.Cwd);
        Assert.False(HarnessSettings.Load(_home).Plugins["@deepseek-ai/dsh-tool-todo.plan"].Permissions!.Write);
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        Assert.Equal("Configured editor contract", tools.Get("str_replace_editor")!.Description);
        var path = Path.Combine(_directory, "view.txt");
        await File.WriteAllTextAsync(path, "abcdefgh", TestContext.Current.CancellationToken);
        var view = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("editor"),
            Name = "str_replace_editor",
            Arguments = JsonSerializer.SerializeToElement(new { command = "view", path }),
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(view.IsError);
        Assert.Contains("<response clipped>", Assert.Single(view.Content.OfType<Dsh.Llm.TextBlock>()).Text);
        var handle = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: _directory), TestContext.Current.CancellationToken);
        using var detach = handle.Dispose;
        await handle.Agent.WhenIdle();
        var todo = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("plan"),
            Name = "todo_write",
            Arguments = JsonDocument.Parse("""{"todos":[{"content":"one","status":"in_progress"},{"content":"two","status":"in_progress"}]}""").RootElement,
            Agent = handle.Agent,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.True(todo.IsError);
        Assert.DoesNotContain(handle.Agent.Session.SnapshotEvents(), entry => entry.Data is TodoWritePayload);
    }

    [Fact]
    public async Task PersistentShell_UsesMigratedConfigWithoutJobsPlugin()
    {
        if (!ShellProbe.Bash)
            Assert.Skip("bash is unavailable");
        var settings = HarnessSettings.Load(_home);
        settings.Plugins["@deepseek-ai/dsh-tool-jobs"] = new PluginSetting { Enabled = false };
        settings.Plugins["@deepseek-ai/dsh-jobs-local"] = new PluginSetting { Enabled = false };
        settings.Plugins["@deepseek-ai/dsh-tool-bash.shell-persistent"] = new PluginSetting
        {
            Parameters = new() { ["maxOutputChars"] = 4L, ["timeoutMs"] = 10000L },
        };
        settings.SavePlugins(_home);
        using var app = await ConfigBoot.Compose(new HarnessOptions(_home, _directory));
        var handle = await app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: _directory), TestContext.Current.CancellationToken);
        using var detach = handle.Dispose;
        await handle.Agent.WhenIdle();
        var result = await app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("persistent"),
            Name = "bash_persistent",
            Arguments = JsonDocument.Parse("""{"command":"printf abcdefgh"}""").RootElement,
            Agent = handle.Agent,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(result.IsError);
        Assert.StartsWith("abcd<response clipped>", Assert.Single(result.Content.OfType<Dsh.Llm.TextBlock>()).Text);
        Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-jobs-local"));
    }

    [Fact]
    public async Task OptionalServices_KeepIndependentConsumersAvailable()
    {
        var settings = HarnessSettings.Load(_home);
        settings.Plugins["@deepseek-ai/dsh-fs-local.fs"] = new PluginSetting { Enabled = false };
        settings.Plugins["@deepseek-ai/dsh-subprocess.shell"] = new PluginSetting { Enabled = false };
        settings.SavePlugins(_home);
        using var app = await ConfigBoot.Compose(new HarnessOptions(_home, _directory));
        var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        Assert.NotNull(tools.Get("read"));
        Assert.NotNull(tools.Get("read_image"));
        Assert.NotNull(tools.Get("glob"));
        Assert.NotNull(tools.Get("todo_write"));
        Assert.NotNull(tools.Get("working_directory"));
        Assert.Null(tools.Get("bash"));
        Assert.Null(tools.Get("bash_persistent"));
        Assert.Null(tools.Get("create_worktree"));
        Assert.Null(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName, false));
        Assert.Equal(ActivationState.Pending, app.Composition!.Find("@deepseek-ai/dsh-tool-worktree.fs")!.State);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("{ enabled: false, allowParallelInProgress: false }")]
    public async Task OldOrMixedConfiguration_IsRejectedWithoutRewritingIt(string oldSetting)
    {
        var yaml = $$"""
            plugins:
              "@deepseek-ai/dsh-tool-todo": {{oldSetting}}
              "@deepseek-ai/dsh-tool-todo.plan": true
            """;
        await File.WriteAllTextAsync(_home.SettingsFile, yaml, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RuntimeException>(() => ConfigBoot.Compose(new HarnessOptions(_home, _directory)));
        Assert.Equal("TOOL_PACKAGE_MIGRATION_REQUIRED", error.Code);
        Assert.Contains("@deepseek-ai/dsh-tool-todo.plan", error.Message);
        Assert.Equal(yaml, await File.ReadAllTextAsync(_home.SettingsFile, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("@deepseek-ai/dsh-tool-bash", "@deepseek-ai/dsh-tool-bash.shell")]
    [InlineData("@deepseek-ai/dsh-subprocess", "@deepseek-ai/dsh-subprocess.shell")]
    [InlineData("@deepseek-ai/dsh-tool-pwsh", "@deepseek-ai/dsh-tool-pwsh.shell")]
    [InlineData("@deepseek-ai/dsh-tool-fs", "@deepseek-ai/dsh-tool-fs.files")]
    [InlineData("@deepseek-ai/dsh-tool-fs-search", "@deepseek-ai/dsh-tool-fs.search")]
    [InlineData("@deepseek-ai/dsh-tool-todo", "@deepseek-ai/dsh-tool-todo.plan")]
    [InlineData("@deepseek-ai/dsh-tool-str-replace-editor", "@deepseek-ai/dsh-tool-str-replace-editor.fs")]
    [InlineData("@deepseek-ai/dsh-fs-local", "@deepseek-ai/dsh-fs-local.fs")]
    [InlineData("@deepseek-ai/dsh-tool-powershell", "@deepseek-ai/dsh-tool-powershell.shell")]
    [InlineData("@deepseek-ai/dsh-tool-wsl", "@deepseek-ai/dsh-tool-wsl.shell")]
    [InlineData("@deepseek-ai/dsh-tool-working-directory", "@deepseek-ai/dsh-tool-working-directory.fs")]
    [InlineData("@deepseek-ai/dsh-tool-worktree", "@deepseek-ai/dsh-tool-worktree.fs")]
    [InlineData("@deepseek-ai/dsh-tool-bash-persistent", "@deepseek-ai/dsh-tool-bash.shell-persistent")]
    [InlineData("@deepseek-ai/dsh-tool-pwsh-persistent", "@deepseek-ai/dsh-tool-pwsh.shell-persistent")]
    public void EveryOldPackage_ReportsItsReplacement(string oldPackage, string newPackage)
    {
        var error = Assert.Throws<RuntimeException>(() => PluginSettingsSection.ParseDocument($"plugins:\n  \"{oldPackage}\": false\n"));
        Assert.Equal("TOOL_PACKAGE_MIGRATION_REQUIRED", error.Code);
        Assert.Contains(newPackage, error.Message);
    }

    [Fact]
    public async Task MalformedNewSetting_IsRejected()
    {
        await File.WriteAllTextAsync(_home.SettingsFile, """
            plugins:
              "@deepseek-ai/dsh-tool-fs.search": { enabled: nope }
            """, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RuntimeException>(() => ConfigBoot.Compose(new HarnessOptions(_home, _directory)));
        Assert.Equal("INVALID_PLUGIN_SETTING", error.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("'invalid'")]
    public async Task MalformedPluginSection_IsRejectedWithoutEnablingDefaults(string value)
    {
        await File.WriteAllTextAsync(_home.SettingsFile, $"plugins: {value}\n", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RuntimeException>(() => ConfigBoot.Compose(new HarnessOptions(_home, _directory)));
        Assert.Equal("INVALID_PLUGIN_SETTING", error.Code);
    }

    [Fact]
    public void CapabilityAssemblies_DoNotReferenceEachOtherOrJobs()
    {
        var assemblies = new[] { typeof(ReadTool).Assembly, typeof(GlobTool).Assembly, typeof(BashTool).Assembly, typeof(TodoWriteTool).Assembly };
        Assert.Equal(4, assemblies.Distinct().Count());
        foreach (var assembly in assemblies)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.StartsWith("Dsh.Tools") || reference.Name == "Dsh.Jobs");
        Assert.Equal(typeof(BashTool).Assembly, typeof(PersistentBashTool).Assembly);
        Assert.NotEqual(typeof(Dsh.Jobs.LocalJobsService).Assembly, typeof(PersistentBashTool).Assembly);
    }
}
