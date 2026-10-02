using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

/** 类型化配置绑定: 标量/嵌套/集合转换、未知字段与类型错配的告警路径。 */
public class PluginConfigBindingTests
{
    public enum SampleMode
    {
        Off,
        On,
    }

    public sealed record NestedConfig
    {
        public string Database { get; init; } = "db";
    }

    public sealed record SampleConfig
    {
        public string Message { get; init; } = "hello";
        public int Count { get; init; } = 3;
        public double Ratio { get; init; } = 1.5;
        public bool Flag { get; init; } = true;
        public SampleMode Mode { get; init; } = SampleMode.Off;
        public IReadOnlySet<string>? Names { get; init; }
        public List<int> Numbers { get; init; } = [];
        public NestedConfig Nested { get; init; } = new();
    }

    [Fact]
    public void Bind_NullConfigYieldsDefaults()
    {
        var bound = PluginConfigBinding.Bind<SampleConfig>(null);
        Assert.Equal("hello", bound.Message);
        Assert.Equal(3, bound.Count);
        Assert.Equal(SampleMode.Off, bound.Mode);
    }

    [Fact]
    public void Bind_TypedInstancePassesThrough()
    {
        var config = new SampleConfig { Message = "kept" };
        Assert.Same(config, PluginConfigBinding.Bind<SampleConfig>(config));
    }

    [Fact]
    public void Bind_ConvertsScalarsAndEnum()
    {
        var bound = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?>
        {
            ["message"] = "hi",
            ["count"] = 42L,
            ["ratio"] = 2.5,
            ["flag"] = false,
            ["mode"] = "on",
        });
        Assert.Equal("hi", bound.Message);
        Assert.Equal(42, bound.Count);
        Assert.Equal(2.5, bound.Ratio);
        Assert.False(bound.Flag);
        Assert.Equal(SampleMode.On, bound.Mode);
    }

    [Fact]
    public void Bind_MatchesKeysCaseInsensitively()
    {
        var bound = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?> { ["MESSAGE"] = "upper" });
        Assert.Equal("upper", bound.Message);
    }

    [Fact]
    public void Bind_NestedRecordRecurses()
    {
        var bound = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["database"] = "nested-db" },
        });
        Assert.Equal("nested-db", bound.Nested.Database);
    }

    [Fact]
    public void Bind_StringSetAcceptsListAndCsv()
    {
        var fromList = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?>
        {
            ["names"] = new List<object?> { "a", "b" },
        });
        Assert.Equal(new HashSet<string> { "a", "b" }, fromList.Names);
        var fromCsv = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?> { ["names"] = "a, b" });
        Assert.Equal(new HashSet<string> { "a", "b" }, fromCsv.Names);
    }

    [Fact]
    public void Bind_NumberListConvertsElements()
    {
        var bound = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?>
        {
            ["numbers"] = new List<object?> { 1L, 2L, 3L },
        });
        Assert.Equal([1, 2, 3], bound.Numbers);
    }

    [Fact]
    public void Bind_UnknownFieldWarnsWithKnownFields()
    {
        var warnings = new List<string>();
        PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?> { ["mesage"] = "typo" }, warnings.Add);
        var warning = Assert.Single(warnings);
        Assert.Contains("mesage", warning);
        Assert.Contains(nameof(SampleConfig.Message), warning);
    }

    [Fact]
    public void Bind_TypeMismatchWarnsAndKeepsDefault()
    {
        var warnings = new List<string>();
        var bound = PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?> { ["count"] = "abc" }, warnings.Add);
        Assert.Equal(3, bound.Count);
        Assert.Contains(warnings, warning => warning.Contains("count") && warning.Contains("Int32"));
    }

    [Fact]
    public void Bind_NestedUnknownFieldWarnsWithDottedPath()
    {
        var warnings = new List<string>();
        PluginConfigBinding.Bind<SampleConfig>(new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["databse"] = "typo" },
        }, warnings.Add);
        Assert.Contains(warnings, warning => warning.Contains("nested.databse"));
    }
}
