using Dsh.Boot;

namespace Dsh.Tests;

/** settings.yaml 损坏时的段级修复: 只丢坏的那一条, 其余段与注释原样保留, 原文隔离保存, 修完写回。 */
public sealed class SettingsRepairTests
{
    private const string BrokenServer = """
        global_default_model: test-provider/test-model

        # 用户注释: 保留
        mcp:
          mobile:
            transport: stdio
            enabled: true
          phone:
            transport: streamable-http
            url: "http://100.75.187.107:47322/mcp/abc"
              enabled: true

        logging:
          level: debug
        """;

    /** 事故原始报文那一类: 一个值把后面的行吞成普通标量(While scanning a plain scalar value, found invalid mapping)。 */
    private const string SwallowedScalar = """
        global_default_model: test-provider/test-model

        mcp:
          mobile:
            transport: stdio
          phone:
            transport: streamable-http
            url: http://127.0.0.1:47322/mcp/abc
              enabled: true

        logging:
          level: debug
        """;

    [Fact]
    public void Load_Drops_Only_The_Broken_Server()
    {
        using var fixture = new Fixture(BrokenServer);

        var settings = HarnessSettings.Load(fixture.Home);

        Assert.True(settings.McpServers.ContainsKey("mobile"));
        Assert.False(settings.McpServers.ContainsKey("phone"));
        Assert.Equal("debug", settings.Logging?.Level);
        Assert.Equal("test-provider", settings.ResolveDefaultModel()?.Provider);
        Assert.Contains("mcp.phone", settings.Notice);
    }

    [Fact]
    public void Load_Keeps_Comments_And_Other_Sections_On_Disk()
    {
        using var fixture = new Fixture(BrokenServer);

        _ = HarnessSettings.Load(fixture.Home);

        var written = File.ReadAllText(fixture.SettingsPath);
        Assert.Contains("# 用户注释: 保留", written);
        Assert.Contains("mobile:", written);
        Assert.DoesNotContain("phone:", written);
        Assert.Contains("level: debug", written);
        Assert.True(HarnessSettings.TryValidate(written, out _));
    }

    [Fact]
    public void Load_Quarantines_The_Original_Text()
    {
        using var fixture = new Fixture(BrokenServer);

        _ = HarnessSettings.Load(fixture.Home);

        var quarantined = Assert.Single(Directory.GetFiles(fixture.Root, "settings.yaml.invalid-*"));
        Assert.Equal(BrokenServer.ReplaceLineEndings("\n") + "\n", File.ReadAllText(quarantined).ReplaceLineEndings("\n"));
    }

    /** 事故原始报文那一类: 值把后面的行吞成普通标量, 修复后同样只丢 phone。 */
    [Fact]
    public void Load_Repairs_A_Value_That_Swallowed_The_Following_Lines()
    {
        using var fixture = new Fixture(SwallowedScalar);

        var settings = HarnessSettings.Load(fixture.Home);

        Assert.True(settings.McpServers.ContainsKey("mobile"));
        Assert.False(settings.McpServers.ContainsKey("phone"));
        Assert.Equal("debug", settings.Logging?.Level);
        Assert.Contains("mcp.phone", settings.Notice);
        Assert.True(HarnessSettings.TryValidate(File.ReadAllText(fixture.SettingsPath), out _));
    }

    /** 写到一半被截断(LLM 输出中断): 只有最后那个没写完的段被丢, 前面的段全部保留。 */
    [Fact]
    public void Load_Drops_A_Section_That_Was_Cut_Off()
    {
        using var fixture = new Fixture("""
            global_default_model: test-provider/test-model
            logging:
              level: debug
            mcp:
              phone:
                url: "http://127.0.0.1:47322/mcp
            """);

        var settings = HarnessSettings.Load(fixture.Home);

        Assert.Equal("debug", settings.Logging?.Level);
        Assert.False(settings.McpServers.ContainsKey("phone"));
        Assert.Contains("mcp", settings.Notice);
    }

    [Fact]
    public void Second_Load_After_Repair_Reports_Nothing()
    {
        using var fixture = new Fixture(BrokenServer);

        _ = HarnessSettings.Load(fixture.Home);
        var reloaded = HarnessSettings.Load(fixture.Home);

        Assert.Null(reloaded.Notice);
        Assert.True(reloaded.McpServers.ContainsKey("mobile"));
    }

    /** 丢弃会放宽权限的段(plugins)不是修复: 拒绝启动并指出行号, 原文照常隔离。 */
    [Fact]
    public void Load_Refuses_To_Guess_Plugin_Permissions()
    {
        using var fixture = new Fixture("""
            plugins:
              "@deepseek-ai/dsh-tui":
                enabled: true
                  permissions: {read: false}
            """);

        var error = Assert.Throws<InvalidDataException>(() => HarnessSettings.Load(fixture.Home));

        Assert.Contains("plugins", error.Message);
        Assert.Single(Directory.GetFiles(fixture.Root, "settings.yaml.invalid-*"));
    }

    [Fact]
    public void Valid_File_Is_Never_Touched()
    {
        using var fixture = new Fixture("""
            global_default_model: test-provider/test-model

            # 用户注释: 保留
            mcp:
              mobile:
                transport: stdio
            """);
        var before = File.ReadAllText(fixture.SettingsPath);

        var settings = HarnessSettings.Load(fixture.Home);

        Assert.Null(settings.Notice);
        Assert.Equal(before, File.ReadAllText(fixture.SettingsPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "settings.yaml.invalid-*"));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string settings)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-repair-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.yaml");
            Home = HarnessHome.Resolve(Root);
            File.WriteAllText(SettingsPath, settings.ReplaceLineEndings("\n") + "\n");
        }

        public string Root { get; }
        public string SettingsPath { get; }
        public HarnessHome Home { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }
}
