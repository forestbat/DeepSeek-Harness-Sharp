using Dsh.Boot;

namespace Dsh.Tests;

/** settings.yaml 生成块由 schema 维护, 键上方的说明注释由人工维护并在 --write-template 时保留。 */
[Collection(RegistryCollection)]
public sealed class SettingsTemplateTests
{
    /** Tests that load plugins with settings share the global settings registry with these checks, so they never overlap. */
    public const string RegistryCollection = "PluginSettingsRegistry";

    /** 随包发布的输出副本(AppContext.BaseDirectory/Profiles/Templates/settings.yaml)。 */
    [Fact]
    public void Output_Template_Has_Generated_Block()
    {
        AssertTemplate(SettingsTemplate.DefaultPath);
    }

    /** 源码模板(用户克隆仓库时看到的那份)。 */
    [Fact]
    public void Source_Template_Has_Generated_Block()
    {
        AssertTemplate(SourceTemplatePath());
    }

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

    private static void AssertTemplate(string path)
    {
        Assert.True(File.Exists(path), path);
        var text = File.ReadAllText(path);
        Assert.Contains("# >>> generated", text);
        Assert.Contains("# <<< generated", text);
        Assert.DoesNotContain("# 映射(见子项)", text);
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
