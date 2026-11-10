using Dsh.Boot;
using Dsh.Plugins;

namespace Dsh.Tests;

/** settings.yaml 生成块由 schema 维护, 键上方的说明注释由人工维护并在 --write-template 时保留。 */
[Collection(RegistryCollection)]
public sealed class SettingsTemplateTests
{
    /** Tests that load plugins with settings share the global settings registry with these checks, so they never overlap. */
    public const string RegistryCollection = "PluginSettingsRegistry";


    [Fact]
    public void Write_Preserves_Manual_Comments()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings-comment-test.yaml");
        File.Copy(SourceTemplatePath(), path, true);
        const string sentinel = "# sentinel: keep this comment";
        var text = File.ReadAllText(path);
        text = text.Replace("# global_default_model:", sentinel + Environment.NewLine + "# global_default_model:", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        _ = SettingsTemplate.Write(path);
        var saved = File.ReadAllText(path);
        Assert.Contains(sentinel, saved);
        Assert.DoesNotContain("# 映射(见子项)", saved);
    }

    [Fact]
    public void Write_RoundTrips_Dictionary_Entries_And_Preserves_Their_Documentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"settings-dictionary-{Guid.NewGuid():N}.yaml");
        var seed = string.Join(Environment.NewLine,
            SettingsSchemaCatalog.GeneratedBegin,
            """
            # providers:
            #   <id>:
            #     provider entry documentation
            #     type:
            #     models:
            #       <id>:
            #         model entry documentation
            #         name:
            # safety:
            #   whitelist:
            #     <id>:
            #       inherited rule documentation
            #       tool: []
            # plugins:
            #   <id>:
            #     referenced setting documentation
            #     enabled: true
            """,
            SettingsSchemaCatalog.GeneratedEnd);
        File.WriteAllText(path, seed);
        try
        {
            Assert.True(SettingsTemplate.Write(path));
            Assert.Null(SettingsTemplate.Verify(path));
            var saved = File.ReadAllText(path);
            Assert.Contains("#     provider entry documentation", saved);
            Assert.Contains("#         model entry documentation", saved);
            Assert.Contains("#       inherited rule documentation", saved);
            Assert.Contains("#     referenced setting documentation", saved);
            Assert.False(SettingsTemplate.Write(path));

            // A misplaced nested dictionary entry is a schema mismatch, not preserved prose.
            File.WriteAllText(path, saved.Replace("#       <id>:", "#     <id>:", StringComparison.Ordinal));
            Assert.NotNull(SettingsTemplate.Verify(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("markdown")]
    [InlineData("template")]
    public void PublicSchemaExports_WithholdSensitiveDefaults_ButKeepEditableKeys(string format)
    {
        var original = Assert.IsAssignableFrom<ISettingsSchemaProvider>(SettingsSchemaCatalog.Document());
        var schema = new SensitiveSchema(original);
        DshPluginSettingsRegistry.Register(schema);
        try
        {
            var output = format == "template" ? SettingsSchemaCatalog.RenderTemplateBlock() : SettingsSchemaCatalog.Render(format);
            Assert.DoesNotContain(SensitiveSchema.PrivateDefault, output);
            Assert.DoesNotContain(SensitiveSchema.ConnectionDefault, output);
            Assert.Contains("api_key", output);
            Assert.Contains("connection_string", output);
            Assert.Contains("ordinary-visible-default", output);
        }
        finally
        {
            DshPluginSettingsRegistry.Register(original);
        }
    }

    /** Synthetic invalid credentials exercise public export privacy without using any real account or endpoint. */
    private sealed class SensitiveSchema(ISettingsSchemaProvider original) : ISettingsSchemaProvider
    {
        public const string PrivateDefault = "test-only-invalid-credential";
        public const string ConnectionDefault = "test-only-private-connection-value";
        public string Package => original.Package;
        public bool IsPlugin => original.IsPlugin;
        public IReadOnlyList<PluginSettingSpec> Settings { get; } =
        [
            new("api_key", PluginSettingKind.Text, $"\"{PrivateDefault}\""),
            new("connection_string", PluginSettingKind.Text, $"\"{ConnectionDefault}\""),
            new("label", PluginSettingKind.Text, "\"ordinary-visible-default\""),
        ];
    }


    private static string SourceTemplatePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Dsh.Boot", "Profiles", "Templates", "settings.yaml");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("找不到源码里的 settings.yaml 模板");
    }
}
