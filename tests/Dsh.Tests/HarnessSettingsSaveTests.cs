using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;

namespace Dsh.Tests;

/** settings.yaml 回写: 只改动有变化的顶层段, 注释、生成块、未知键与 plugins 段原样保留。 */
public sealed class HarnessSettingsSaveTests
{
    private const string Settings = """
        # 用户注释: 保留
        global_default_model: alpha/model-a

        custom_key: keep-me

        providers:
          alpha:
            type: openai-compatible
            options:
              baseUrl: https://alpha.example
              apiKey: alpha-key
          beta:
            type: openai-compatible
            options:
              baseUrl: https://beta.example
              apiKey: beta-key

        safety:
          autoApprove: false
          blacklist:
          - rm -rf

        a2a:
          enabled: true
          port: 4100

        logging:
          level: debug

        storage:
          root: /data/dsh

        plugins:
          "@deepseek-ai/dsh-memory": false

        # >>> generated: settings-schema (do not edit; run: dsharp config schema --write-template)
        # safety:  # 映射(见子项)
        #   autoApprove:
        # <<< generated
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Save_RewritesOnlyChangedSections(string newline)
    {
        var home = CreateHome(newline);
        try
        {
            var settings = HarnessSettings.Load(home);
            settings.Safety!.AutoApprove = true;

            settings.Save(home);

            var text = ReadSettings(home);
            Assert.Equal(text.ReplaceLineEndings(newline), text);
            Assert.Contains("# 用户注释: 保留", text);
            Assert.Contains("custom_key: keep-me", text);
            Assert.Contains("""
                # >>> generated: settings-schema (do not edit; run: dsharp config schema --write-template)
                # safety:  # 映射(见子项)
                #   autoApprove:
                # <<< generated
                """.ReplaceLineEndings(newline), text);
            Assert.Contains("\"@deepseek-ai/dsh-memory\": false", text);
            Assert.Contains("autoApprove: true", text);
            Assert.DoesNotContain("autoApprove: false", text);
            var reloaded = HarnessSettings.Load(home);
            Assert.True(reloaded.Safety!.AutoApprove);
            Assert.Equal(["rm -rf"], reloaded.Safety.Blacklist);
            Assert.Equal(4100, reloaded.A2a!.Port);
            Assert.Equal("alpha-key", reloaded.Providers["alpha"].Options!.ApiKey);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Fact]
    public void Load_MissingSettings_DoesNotWriteTemplate()
    {
        var home = CreateHome("\n");
        try
        {
            var active = Path.Combine(home.Root, "settings.yaml");
            File.Delete(active);

            var settings = HarnessSettings.Load(home);

            Assert.NotNull(settings.Plugins);
            Assert.False(File.Exists(active));
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }


    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SavePlugins_KeepsTheFileLineEndings(string newline)
    {
        var home = CreateHome(newline);
        try
        {
            var settings = HarnessSettings.Load(home);
            settings.Plugins["@deepseek-ai/dsh-memory"] = new PluginSetting { Enabled = true };

            settings.SavePlugins(home);

            var text = ReadSettings(home);
            Assert.Equal(text.ReplaceLineEndings(newline), text);
            Assert.Contains("\"@deepseek-ai/dsh-memory\": true", text);
            Assert.Contains("custom_key: keep-me", text);
            Assert.True(HarnessSettings.Load(home).Plugins["@deepseek-ai/dsh-memory"].Enabled);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SavePlugins_PreservesNestedUnknownKeysAndComments_AndBacksUpTheOriginal(string newline)
    {
        var home = CreateHome(newline);
        const string original = """
            # document comment
            plugins:
              "@deepseek-ai/dsh-ide": # package comment
                enabled: true
                future_option: keep # unknown option comment
                history:
                  # history comment
                  timeline: false # value comment
                  future_history: [a, b] # future nested comment
              "@acme/other": true # other package comment
            custom: retained
            """;
        try
        {
            File.WriteAllText(home.SettingsFile, original.ReplaceLineEndings(newline));
            var settings = HarnessSettings.Load(home);
            var history = Assert.IsAssignableFrom<IDictionary<string, object?>>(settings.Plugins["@deepseek-ai/dsh-ide"].Parameters["history"]);
            history["timeline"] = true;

            settings.SavePlugins(home);

            var text = ReadSettings(home);
            Assert.Contains("timeline: true # value comment", text);
            Assert.Contains("future_option: keep # unknown option comment", text);
            Assert.Contains("future_history: [a, b] # future nested comment", text);
            Assert.Contains("# history comment", text);
            Assert.Contains("\"@deepseek-ai/dsh-ide\": # package comment", text);
            Assert.Contains("\"@acme/other\": true # other package comment", text);
            Assert.Equal(original.ReplaceLineEndings(newline), File.ReadAllText($"{home.SettingsFile}.bak"));
            Assert.Equal(text.ReplaceLineEndings(newline), text);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Fact]
    public async Task ProviderRemove_KeepsSectionsTheCommandDoesNotOwn()
    {
        var home = CreateHome("\n");
        try
        {
            var ctx = new Context();
            _ = new SystemPrompt(ctx, new SystemPromptConfig());
            var commands = CommandsService.Register(ctx);
            using var registration = ProviderCommand.Register(ctx, home);

            var removed = await commands.Execute(new TerminalFakeAgent(ctx, new AgentRegistry(ctx), home.Root), "/provider remove beta", TestContext.Current.CancellationToken);

            Assert.IsType<CommandResult.Success>(removed!.Result);
            var reloaded = HarnessSettings.Load(home);
            Assert.Equal(["alpha"], reloaded.Providers.Keys);
            Assert.True(reloaded.A2a!.Enabled);
            Assert.Equal("debug", reloaded.Logging!.Level);
            Assert.Equal("/data/dsh", reloaded.Storage!.Root);
            var text = ReadSettings(home);
            Assert.Contains("custom_key: keep-me", text);
            Assert.Contains("# 用户注释: 保留", text);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    private static HarnessHome CreateHome(string newline)
    {
        var home = HarnessHome.Resolve(TempTree.CreateDirectory("settings-save"));
        File.WriteAllText(Path.Combine(home.Root, "settings.yaml"), Settings.ReplaceLineEndings(newline));
        return home;
    }

    private static string ReadSettings(HarnessHome home)
        => File.ReadAllText(Path.Combine(home.Root, "settings.yaml"));
}
