using Dsh.Boot;
using Dsh.Interaction;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Tui;

namespace Dsh.Tests;

/** Sandbox switch and plugins."<package>".permissions: parsing, typed read model and the /plugins write-back. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class PluginPermissionTests
{
    private const string Todo = "@deepseek-ai/dsh-tool-todo";
    private const string OuterComment = "# keep: comment above the plugins block";
    private const string TrailingComment = "# keep: comment after the plugins block";
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly string SettingsWithComments = $"""
        {OuterComment}
        logging:
          file: false
        plugins:
          "{Todo}": true
        {TrailingComment}
        rules: []
        """;

    [Fact]
    public void Permissions_DefaultToAllowed_AndStayOutOfPluginParameters()
    {
        var plugins = PluginSettingsSection.ParseDocument("""
            plugins:
              "@a/plain": true
              "@a/partial":
                enabled: true
                permissions:
                  write: false
                timeout: 5
            """);

        Assert.Null(plugins["@a/plain"].Permissions);
        Assert.Equal(new PluginPermissions { Write = false }, plugins["@a/partial"].Permissions);
        Assert.True(plugins["@a/partial"].Permissions!.Read);
        Assert.True(plugins["@a/partial"].Permissions!.Network);
        Assert.Equal(["timeout"], plugins["@a/partial"].Parameters.Keys);
    }

    [Theory]
    [InlineData("permissions: true")]
    [InlineData("permissions:\n      wirte: false")]
    [InlineData("permissions:\n      write: nope")]
    public void Permissions_RejectMalformedValues(string permissions)
    {
        var yaml = $"plugins:\n  \"@a/x\":\n    {permissions}\n";

        var error = Assert.Throws<RuntimeException>(() => PluginSettingsSection.ParseDocument(yaml));

        Assert.Equal("INVALID_PLUGIN_SETTING", error.Code);
    }

    [Fact]
    public void Sandbox_IsOffInTheTemplate_AndReadsExplicitValue()
    {
        var root = TempTree.CreateDirectory("dsh-sandbox");
        try
        {
            var fresh = HarnessHome.Resolve(Path.Combine(root, "fresh"));
            Assert.False(HarnessSettings.Load(fresh).Sandbox!.Enabled);

            var explicitHome = HarnessHome.Resolve(Path.Combine(root, "explicit"));
            Directory.CreateDirectory(explicitHome.Root);
            File.WriteAllText(Path.Combine(explicitHome.Root, "settings.yaml"), "sandbox:\n  enabled: true\n");
            Assert.True(HarnessSettings.Load(explicitHome).Sandbox!.Enabled);

            var absentHome = HarnessHome.Resolve(Path.Combine(root, "absent"));
            Directory.CreateDirectory(absentHome.Root);
            File.WriteAllText(Path.Combine(absentHome.Root, "settings.yaml"), "rules: []\n");
            Assert.Null(HarnessSettings.Load(absentHome).Sandbox);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** ban_write then enable_write round trip through the real command, with enable/disable and remove in between. */
    [Fact]
    public async Task BanWrite_EnableWrite_RoundTrip_PreservesComments()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(SettingsWithComments);
        var home = environment.App.Home;

        Assert.Contains("denied", await RunAsync(environment, $"/plugins ban_write {Todo}"));
        var banned = HarnessSettings.Load(home).Plugins[Todo];
        Assert.Equal(new PluginPermissions { Write = false }, banned.Permissions);
        Assert.DoesNotContain(PluginSetting.PermissionsKey, banned.Parameters.Keys);
        AssertCommentsKept(home);

        await RunAsync(environment, $"/plugins disable {Todo}");
        await RunAsync(environment, $"/plugins enable {Todo}");
        Assert.Equal(new PluginPermissions { Write = false }, HarnessSettings.Load(home).Plugins[Todo].Permissions);

        Assert.Contains("allowed", await RunAsync(environment, $"/plugins enable_write {Todo}"));
        Assert.Equal(PluginPermissions.AllAllowed, HarnessSettings.Load(home).Plugins[Todo].Permissions);
        AssertCommentsKept(home);

        await RunAsync(environment, $"/plugins remove {Todo}");
        var removed = HarnessSettings.Load(home).Plugins[Todo];
        Assert.False(removed.Enabled);
        Assert.Null(removed.Permissions);
        Assert.Empty(removed.Parameters);
        AssertCommentsKept(home);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("network")]
    public async Task BrokerPermissionCommands_PreserveOtherPermissions_AndComments(string permission)
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(SettingsWithComments);
        var home = environment.App.Home;
        await RunAsync(environment, $"/plugins ban_write {Todo}");

        Assert.Contains("denied", await RunAsync(environment, $"/plugins ban_{permission} {Todo}"));
        var denied = HarnessSettings.Load(home).Plugins[Todo];
        Assert.False(denied.Permissions!.Write);
        Assert.Equal(permission != "read", denied.Permissions.Read);
        Assert.Equal(permission != "network", denied.Permissions.Network);
        Assert.DoesNotContain(PluginSetting.PermissionsKey, denied.Parameters.Keys);
        AssertCommentsKept(home);

        Assert.Contains("allowed", await RunAsync(environment, $"/plugins enable_{permission} {Todo}"));
        Assert.Equal(new PluginPermissions { Write = false }, HarnessSettings.Load(home).Plugins[Todo].Permissions);
        AssertCommentsKept(home);
    }


    [Fact]
    public async Task BanWrite_ReportsUnknownPackage_AndUsage()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(SettingsWithComments);

        Assert.Contains("not found", await RunAsync(environment, "/plugins ban_write @acme/missing"));
        var commands = environment.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var usage = await commands.Execute(environment.Agent, "/plugins ban_write", TestContext.Current.CancellationToken);
        Assert.Contains("ban_write <pkg>", Assert.IsType<CommandResult.Error>(usage!.Result).Text);
    }

    /** The TUI overlay selects a broker permission command and completes an installed package before submission. */
    [Theory]
    [InlineData("ban_read", false, true, true)]
    [InlineData("ban_write", true, false, true)]
    [InlineData("ban_network", true, true, false)]
    [InlineData("enable_read", true, false, false)]
    [InlineData("enable_write", false, true, false)]
    [InlineData("enable_network", false, false, true)]
    public async Task TuiOverlay_PermissionCommand_PicksPackage_AndWritesSettings(string command, bool read, bool write, bool network)
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(SettingsWithComments);
        if (command.StartsWith("enable_", StringComparison.Ordinal))
        {
            await RunAsync(environment, $"/plugins ban_read {Todo}");
            await RunAsync(environment, $"/plugins ban_write {Todo}");
            await RunAsync(environment, $"/plugins ban_network {Todo}");
        }
        using var chat = new ChatWindow(environment.App.Ctx, environment.Agent, environment.App.Home);

        Type(chat, "/plugins");
        Press(chat, ConsoleKey.Tab);
        var menu = chat.InputPane.CommandMenu!;
        Assert.Equal(CommandMenuState.MenuStage.Subcommand, menu.Stage);
        Assert.Contains(command, menu.Candidates);

        Type(chat, command);
        Press(chat, ConsoleKey.Tab);
        Assert.Equal(CommandMenuState.MenuStage.Argument, menu.Stage);
        Assert.Contains(Todo, menu.Candidates);

        Type(chat, "dsh-tool-todo");
        Press(chat, ConsoleKey.Enter);
        Assert.Equal($"/plugins {command} {Todo}", chat.InputPane.Input);
        Press(chat, ConsoleKey.Enter);
        var permissions = await WaitForPermissionsAsync(environment.App.Home, new PluginPermissions { Read = read, Write = write, Network = network });
        Assert.Equal(new PluginPermissions { Read = read, Write = write, Network = network }, permissions);
    }

    /** The overlay persists submitted broker settings asynchronously within the original consumer deadline. */
    private static async Task<PluginPermissions?> WaitForPermissionsAsync(HarnessHome home, PluginPermissions expected)
    {
        var deadline = DateTime.UtcNow + SubmitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (HarnessSettings.Load(home).Plugins.GetValueOrDefault(Todo)?.Permissions is { } permissions && permissions == expected)
                return permissions;
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
        return null;
    }

    private static async Task<string> RunAsync(GuiTestEnvironment environment, string line)
    {
        var commands = environment.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var execution = await commands.Execute(environment.Agent, line, TestContext.Current.CancellationToken);
        return Assert.IsType<CommandResult.Success>(execution!.Result).Text ?? "";
    }

    private static void AssertCommentsKept(HarnessHome home)
    {
        var text = File.ReadAllText(SettingsPath(home));
        Assert.Contains(OuterComment, text);
        Assert.Contains(TrailingComment, text);
    }

    private static string SettingsPath(HarnessHome home) => Path.Combine(home.Root, "settings.yaml");

    private static void Type(ChatWindow chat, string text)
    {
        foreach (var character in text)
            chat.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
    }

    private static void Press(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false));
}

