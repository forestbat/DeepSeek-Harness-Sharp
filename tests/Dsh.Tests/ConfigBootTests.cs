using System.Text.Json;
using Dsh.Llm;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Tools;

namespace Dsh.Tests;

public class ConfigBootTests
{
    [Fact]
    public async Task Compose_ActivatesConfiguredPluginsAndMergesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                global_default_model: deepseek-official/deepseek-v4-flash
                plugins:
                  "@deepseek-ai/dsh-core": true
                  "@deepseek-ai/dsh-interaction": true
                  "@deepseek-ai/dsh-persistence": true
                  "@deepseek-ai/dsh-subprocess": true
                  "@deepseek-ai/dsh-tool-bash": true
                  "@deepseek-ai/dsh-tool-fs": true
                  "@deepseek-ai/dsh-tool-fs-search":
                    sampleOverCapGlobResults: false
                  "@deepseek-ai/dsh-tool-todo":
                    allowParallelInProgress: true
                  "@deepseek-ai/dsh-fs-local": true
                  "@deepseek-ai/dsh-tool-str-replace-editor":
                    maxOutputChars: 16000
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var missing = new[] { "bash", "read", "write", "edit", "glob", "grep", "todo_write", "str_replace_editor" }
                .Where(name => tools.Get(name) is null)
                .ToList();
            Assert.True(missing.Count == 0, "missing tools: " + string.Join(", ", missing));

            Assert.NotNull(app.Ctx.Get<LocalFsService>(LocalFsService.ServiceName));

            // 用户 settings.yaml 未列出的插件发现即启用
            Assert.Equal(ActivationState.Active, app.Composition!.Find("@deepseek-ai/dsh-checkpoints")?.State);
            Assert.Equal(ActivationState.Active, app.Composition.Find("@deepseek-ai/dsh-ide")?.State);

            var fsSearch = app.Composition.Find("@deepseek-ai/dsh-tool-fs-search")!;
            Assert.Equal(false, (fsSearch.Config as IDictionary<string, object?>)?["sampleOverCapGlobResults"]);
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

    [Fact(Timeout = 60000)]
    public async Task StartupImageTcb_ActivatesFileRulesAndEnforcesActualProductionTools()
    {
        if (await PolicyProcessIsolation.RunIfNeededAsync(
            "Dsh.Tests.ConfigBootTests.StartupImageTcb_ActivatesFileRulesAndEnforcesActualProductionTools",
            TestContext.Current.CancellationToken)) return;
        foreach (var scalarTool in new[] { false, true })
            await ExerciseStartupPolicyAsync(scalarTool);
    }

    private static async Task ExerciseStartupPolicyAsync(bool scalarTool)
    {
        var root = TempTree.CreateDirectory("policy-product");
        var signal = TestContext.Current.CancellationToken;
        try
        {
            var home = new HarnessHome(Path.Combine(root, "home"));
            Directory.CreateDirectory(home.Root);
            var publicFile = Path.Combine(root, "public.txt");
            var secretFile = Path.Combine(root, "secret.txt");
            await File.WriteAllTextAsync(publicFile, "public original", signal);
            await File.WriteAllTextAsync(secretFile, "secret original", signal);
            var tool = scalarTool ? "read" : "[read]";
            await File.WriteAllTextAsync(home.SettingsFile, $$"""
                safety:
                  autoApprove: false
                  blacklist:
                    private-file: { path: {{JsonSerializer.Serialize(secretFile)}}, intent: any }
                  whitelist:
                    workspace-read:
                      tool: {{tool}}
                      path: {{JsonSerializer.Serialize(Path.Combine(root, "**"))}}
                      intent: read
                    workspace-write:
                      tool: [write, edit]
                      path: {{JsonSerializer.Serialize(Path.Combine(root, "**"))}}
                      intent: any
                """, signal);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root,
                PluginsDirectory: Path.Combine(root, "plugins")));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            using var agent = new AgentLoopAgent(app.Ctx, id, new AgentOptions(), session, _ => 0);
            session.Append(new TurnStartPayload(1));
            Task<ToolExecutionResult> Execute(string name, object arguments) => tools.Execute(new ToolExecutionInput
            {
                Name = name, Arguments = JsonSerializer.SerializeToElement(arguments), Agent = agent,
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Signal = signal,
            });
            var read = await Execute("read", new { file_path = publicFile });
            Assert.False(read.IsError, string.Concat(read.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Contains("public original", string.Concat(read.Content.OfType<TextBlock>().Select(block => block.Text)));
            var write = await Execute("write", new { file_path = publicFile, content = "public replacement" });
            Assert.False(write.IsError, string.Concat(write.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.Equal("public replacement", await File.ReadAllTextAsync(publicFile, signal));
            var deniedRead = await Execute("read", new { file_path = secretFile });
            Assert.True(deniedRead.IsError);
            Assert.DoesNotContain("secret original", string.Concat(deniedRead.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.True((await Execute("write", new { file_path = secretFile, content = "not committed" })).IsError);
            Assert.Equal("secret original", await File.ReadAllTextAsync(secretFile, signal));
            var editable = HarnessSettings.Load(home);
            editable.Safety!.Blacklist["hot-read-deny"] = new() { Path = publicFile, Intent = "read" };
            editable.Save(home);
            Assert.True((await Execute("read", new { file_path = publicFile })).IsError);
            var activation = Assert.IsType<SettingsPolicyActivation>(app.Ctx.GetProp("safetyPolicyActivation"));
            Assert.False(activation.Status.Rejected);
            var mutable = HarnessSettings.LoadForReview(home);
            mutable.Safety!.Blacklist.Clear();
            Assert.True((await Execute("read", new { file_path = publicFile })).IsError);
            Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload
                { RuleId: "hot-read-deny", Decision: "deny", Tool: "read", Intent: "read" });
        }
        finally { TempTree.Delete(root); }
    }

    [Fact]
    public async Task Compose_SkipsDisabledPlugins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), """
                plugins:
                  "@deepseek-ai/dsh-tool-todo": false
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            Assert.Null(app.Composition!.Find("@deepseek-ai/dsh-tool-todo"));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            Assert.Null(tools.Get("todo_write"));
            Assert.NotNull(tools.Get("bash"));
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

    [Fact]
    public async Task Compose_StorageRootRedirectsHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-configboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HarnessApp? app = null;
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(dir, "home"));
            Directory.CreateDirectory(home.Root);
            var target = Path.Combine(dir, "data");
            await File.WriteAllTextAsync(Path.Combine(home.Root, "settings.yaml"), $"""
                global_default_model: deepseek-official/deepseek-v4-flash
                storage:
                  root: {target.Replace('\\', '/')}
                plugins:
                  "@deepseek-ai/dsh-core": true
                """, TestContext.Current.CancellationToken);

            app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: dir));

            Assert.Equal(Path.GetFullPath(target), app.Home.Root);
            Assert.Equal(Path.GetFullPath(target), (string)app.Ctx.GetProp("dshHomePath")!);
            Assert.True(File.Exists(Path.Combine(target, "settings.yaml")));
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
