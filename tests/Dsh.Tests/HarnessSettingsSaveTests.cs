using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Runtime;
using Dsh.Agent;

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
            deny-delete:
              dialect: posix
              args: { command: "rm -rf" }

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

            Assert.Equal(["settings.yaml"], Directory.GetFiles(home.Root).Select(Path.GetFileName));
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
            Assert.Equal("rm -rf", reloaded.Safety.Blacklist["deny-delete"].Args["command"]);
            Assert.Equal(4100, reloaded.A2a!.Port);
            Assert.Equal("alpha-key", reloaded.Providers["alpha"].Options!.ApiKey);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Fact]
    public void MultiPackageUpdate_UsesCurrentDocument_AndRetainsUnrelatedPluginChanges()
    {
        var home = CreateHome("\n");
        try
        {
            var stale = HarnessSettings.Load(home);
            HarnessSettings.UpdatePlugin(home, "test/concurrent", _ => new PluginSetting
            {
                Enabled = false,
                Parameters = new Dictionary<string, object?> { ["marker"] = "current" },
            });
            HarnessSettings.UpdatePlugin(home, "test/first", _ => new PluginSetting
            {
                Enabled = false,
                Execution = PluginExecution.Sandbox,
                Permissions = new PluginPermissions { Network = false },
                Parameters = new Dictionary<string, object?> { ["limit"] = 7 },
            });

            HarnessSettings.UpdatePlugins(home, ["test/first", "test/second"], existing =>
                (existing ?? new PluginSetting()) with { Enabled = true });

            var actual = HarnessSettings.Load(home);
            Assert.DoesNotContain("test/concurrent", stale.Plugins.Keys);
            Assert.False(actual.Plugins["test/concurrent"].Enabled);
            Assert.Equal("current", actual.Plugins["test/concurrent"].Parameters["marker"]);
            Assert.True(actual.Plugins["test/first"].Enabled);
            Assert.True(actual.Plugins["test/second"].Enabled);
            Assert.Equal(PluginExecution.Sandbox, actual.Plugins["test/first"].Execution);
            Assert.False(actual.Plugins["test/first"].Permissions!.Network);
            Assert.Equal(7, PluginConfigBinding.Bind<LimitConfig>(actual.Plugins["test/first"].Parameters).Limit);
            var text = ReadSettings(home);
            Assert.Contains("# 用户注释: 保留", text);
            Assert.Contains("custom_key: keep-me", text);
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    /** 旧版本把文件写成了大写 SETTINGS.YAML: 新版本用小写名落盘只能接管它, 不能把内容丢掉。 */
    [Fact]
    public void Save_TakesOverTheUppercaseFileLeftByOlderBuilds()
    {
        var home = CreateHome("\n");
        try
        {
            var uppercase = Path.Combine(home.Root, "SETTINGS.YAML");
            File.Move(home.SettingsFile, uppercase);

            var settings = HarnessSettings.Load(home);
            settings.Safety!.AutoApprove = true;
            settings.Save(home);

            var text = ReadSettings(home);
            Assert.Contains("# 用户注释: 保留", text);
            Assert.Contains("custom_key: keep-me", text);
            Assert.Contains("autoApprove: true", text);
            Assert.Equal(["settings.yaml"], Directory.GetFiles(home.Root).Select(Path.GetFileName));
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_RecoversLegacySettingsWithoutChangingTheirBytes(bool forReview)
    {
        var home = CreateHome("\r\n");
        try
        {
            var original = File.ReadAllBytes(home.SettingsFile);
            File.Move(home.SettingsFile, Path.Combine(home.Root, "SETTINGS.YAML"));

            var settings = forReview ? HarnessSettings.LoadForReview(home) : HarnessSettings.Load(home);

            Assert.Equal("alpha/model-a", settings.GlobalDefaultModel);
            Assert.False(settings.Safety!.AutoApprove);
            Assert.False(settings.Plugins["@deepseek-ai/dsh-memory"].Enabled);
            Assert.Equal(original, File.ReadAllBytes(home.SettingsFile));
            Assert.Equal(["settings.yaml"], Directory.GetFiles(home.Root).Select(Path.GetFileName));
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_AfterRecoveringLegacySettingsLink_PreservesAnotherWritersChanges(bool forReview)
    {
        var home = CreateHome("\n");
        var target = CreateHome("\n");
        try
        {
            File.Delete(home.SettingsFile);
            File.CreateSymbolicLink(Path.Combine(home.Root, "SETTINGS.YAML"), target.SettingsFile);
            var settings = forReview ? HarnessSettings.LoadForReview(home) : HarnessSettings.Load(home);

            Assert.True(HarnessSettings.Update(target, current =>
            {
                current.GlobalDefaultModel = "beta/model-b";
                return true;
            }));
            settings.Safety!.AutoApprove = true;
            settings.Save(home);

            var actual = HarnessSettings.Load(home);
            Assert.Equal("beta/model-b", actual.GlobalDefaultModel);
            Assert.True(actual.Safety!.AutoApprove);
            Assert.NotNull(new FileInfo(home.SettingsFile).LinkTarget);
            var savedTarget = HarnessSettings.Load(target);
            Assert.Equal("beta/model-b", savedTarget.GlobalDefaultModel);
            Assert.True(savedTarget.Safety!.AutoApprove);
        }
        catch (Exception error) when (OperatingSystem.IsWindows()
            && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
        {
            Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
        }
        finally
        {
            TempTree.Delete(home.Root);
            TempTree.Delete(target.Root);
        }
    }

    [Fact]
    public async Task ApprovedMutation_PreservesSettingsLinkAndTargetFileName()
    {
        var home = CreateHome("\n");
        var target = CreateHome("\n");
        const string targetName = "SharedSettings.YaMl";
        var targetPath = Path.Combine(target.Root, targetName);
        try
        {
            File.Move(target.SettingsFile, targetPath);
            File.Delete(home.SettingsFile);
            File.CreateSymbolicLink(home.SettingsFile, targetPath);
            var before = File.ReadAllText(targetPath);
            var after = before.Replace("alpha/model-a", "beta/model-b", StringComparison.Ordinal);

            await HarnessSettings.CommitApprovedMutationAsync(home.SettingsFile, before, after,
                (temporary, signal) => File.WriteAllTextAsync(temporary, after, signal), TestContext.Current.CancellationToken);

            Assert.NotNull(new FileInfo(home.SettingsFile).LinkTarget);
            Assert.Equal(after, File.ReadAllText(targetPath));
            Assert.Equal(after, ReadSettings(home));
            Assert.Equal([targetName], Directory.GetFiles(target.Root).Select(Path.GetFileName));
        }
        catch (Exception error) when (OperatingSystem.IsWindows()
            && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
        {
            Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
        }
        finally
        {
            TempTree.Delete(home.Root);
            TempTree.Delete(target.Root);
        }
    }

    [Fact]
    public void SavePlugins_PreservesSettingsLinkAndUpdatesItsTarget()
    {
        var home = CreateHome("\n");
        var target = CreateHome("\n");
        try
        {
            File.Delete(home.SettingsFile);
            File.CreateSymbolicLink(home.SettingsFile, target.SettingsFile);
            var settings = HarnessSettings.Load(home);
            settings.Plugins["@deepseek-ai/dsh-memory"] = new PluginSetting { Enabled = true };

            settings.SavePlugins(home);

            Assert.NotNull(new FileInfo(home.SettingsFile).LinkTarget);
            Assert.True(HarnessSettings.Load(target).Plugins["@deepseek-ai/dsh-memory"].Enabled);
            Assert.Equal(ReadSettings(target), ReadSettings(home));
            Assert.Equal(["settings.yaml"], Directory.GetFiles(target.Root).Select(Path.GetFileName));
        }
        catch (Exception error) when (OperatingSystem.IsWindows()
            && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) == 1314)
        {
            Assert.Skip("The test account lacks the Windows symbolic-link creation privilege.");
        }
        finally
        {
            TempTree.Delete(home.Root);
            TempTree.Delete(target.Root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_PrefersExistingSettingsAndPreservesADistinctLegacyFile(bool forReview)
    {
        var home = CreateHome("\n");
        try
        {
            var uppercase = Path.Combine(home.Root, "SETTINGS.YAML");
            var distinctLegacyFile = !File.Exists(uppercase);
            const string legacy = "# legacy document\nglobal_default_model: legacy/model\n";
            if (distinctLegacyFile)
                File.WriteAllText(uppercase, legacy);
            var original = File.ReadAllBytes(home.SettingsFile);

            var settings = forReview ? HarnessSettings.LoadForReview(home) : HarnessSettings.Load(home);

            Assert.Equal("alpha/model-a", settings.GlobalDefaultModel);
            Assert.Equal(original, File.ReadAllBytes(home.SettingsFile));
            if (distinctLegacyFile)
                Assert.Equal(legacy, File.ReadAllText(uppercase));
        }
        finally
        {
            TempTree.Delete(home.Root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_MissingSettings_DoesNotWriteTemplate(bool forReview)
    {
        var home = CreateHome("\n");
        try
        {
            var active = Path.Combine(home.Root, "settings.yaml");
            File.Delete(active);

            var settings = forReview ? HarnessSettings.LoadForReview(home) : HarnessSettings.Load(home);

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
            Assert.Equal(["settings.yaml", "settings.yaml.bak"], Directory.GetFiles(home.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
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

    public sealed class LimitConfig
    {
        public int Limit { get; set; }
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
