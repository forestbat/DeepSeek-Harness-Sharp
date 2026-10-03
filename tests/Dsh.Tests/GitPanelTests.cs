using Dsh.Compaction;
using Dsh.Tui;

namespace Dsh.Tests;

public sealed class CompactionModelSettingTests
{
    [Fact]
    public void Parse_Splits_Provider_And_Model()
    {
        var parsed = CompactionModelSetting.Parse("openai/gpt-4o");

        Assert.True(parsed is not null);
        Assert.Equal("openai", parsed.Value.Provider);
        Assert.Equal("gpt-4o", parsed.Value.Model);
    }

    [Fact]
    public void Parse_ModelOnly_Or_Empty_Returns_Null()
    {
        Assert.Null(CompactionModelSetting.Parse("gpt-4o"));
        Assert.Null(CompactionModelSetting.Parse("openai/"));
        Assert.Null(CompactionModelSetting.Parse("  "));
        Assert.Null(CompactionModelSetting.Parse(null));
    }

    [Fact]
    public void ModelName_Strips_Optional_Provider()
    {
        Assert.Equal("gpt-4o", CompactionModelSetting.ModelName("openai/gpt-4o"));
        Assert.Equal("gpt-4o", CompactionModelSetting.ModelName("gpt-4o"));
    }
}

public sealed class GitPanelTests
{
    [Fact]
    public void Format_SortsAndRendersNumstat()
    {
        const string numstat = "3\t1\tsrc/b.cs\n-\t-\tassets/logo.png\n2\t0\tsrc/a.cs\n";

        var lines = GitPanel.Format(numstat, "未提交改动");

        Assert.Equal("未提交改动", lines[0]);
        Assert.Equal("assets/logo.png (binary)", lines[1]);
        Assert.Equal("src/a.cs 2+ 0-", lines[2]);
        Assert.Equal("src/b.cs 3+ 1-", lines[3]);
    }

    [Fact]
    public void Format_TruncatesBeyondMaxRows()
    {
        var numstat = string.Join('\n', Enumerable.Range(0, GitPanel.MaxRows + 3).Select(index => $"1\t0\tfile{index:D2}.cs"));

        var lines = GitPanel.Format(numstat, "上次提交");

        Assert.Equal(1 + GitPanel.MaxRows + 1, lines.Count);
        Assert.Equal("… (+3)", lines[^1]);
    }
}
