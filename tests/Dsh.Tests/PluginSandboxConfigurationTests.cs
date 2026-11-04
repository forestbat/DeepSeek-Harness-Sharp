using System.Text.Json;
using Dsh.Plugins.Sandbox;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class PluginSandboxConfigurationTests
{
    [Fact]
    public void WorkerConfiguration_BindsNestedSettingsWithoutNumericPrecisionLoss()
    {
        const string text = "quote\" slash\\ newline\n tab\t control\u0001 中文";
        var values = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["enabled"] = true,
            ["count"] = int.MinValue,
            ["signed"] = long.MinValue,
            ["unsigned"] = ulong.MaxValue,
            ["amount"] = decimal.MaxValue,
            ["ratio"] = 1e300,
            ["missing"] = null,
            ["nested"] = new Dictionary<string, object?>
            {
                ["label"] = text,
                ["numbers"] = new int[] { 1, -2, int.MaxValue },
                ["enabled"] = false,
            },
        };
        var wire = JsonSerializer.SerializeToElement(values, PluginConfigurationJsonContext.Default.Object);
        var config = PluginConfigBinding.Bind<WorkerConfiguration>(PluginSandboxWorkerWire.Configuration(wire));
        Assert.Equal(text, config.Text);
        Assert.True(config.Enabled);
        Assert.Equal(int.MinValue, config.Count);
        Assert.Equal(long.MinValue, config.Signed);
        Assert.Equal(ulong.MaxValue, config.Unsigned);
        Assert.Equal(decimal.MaxValue, config.Amount);
        Assert.Equal(1e300, config.Ratio);
        Assert.Null(config.Missing);
        Assert.Equal(text, config.Nested.Label);
        Assert.Equal([1, -2, int.MaxValue], config.Nested.Numbers);
        Assert.False(config.Nested.Enabled);
    }

    [Fact]
    public void FloatingPointConfiguration_PreservesTinyAndSubnormalValues()
    {
        var values = new Dictionary<string, object?>
        {
            ["small"] = 1e-30,
            ["subnormal"] = double.Epsilon,
            ["rounded"] = 1.2345678901234567e-28,
        };
        var wire = JsonSerializer.SerializeToElement(values, PluginConfigurationJsonContext.Default.Object);
        var config = PluginConfigBinding.Bind<FloatingConfiguration>(PluginSandboxWorkerWire.Configuration(wire));
        Assert.Equal(1e-30, config.Small);
        Assert.Equal(double.Epsilon, config.Subnormal);
        Assert.Equal(1.2345678901234567e-28, config.Rounded);
    }

    public sealed class WorkerConfiguration
    {
        public string Text { get; set; } = "";
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public long Signed { get; set; }
        public ulong Unsigned { get; set; }
        public decimal Amount { get; set; }
        public double Ratio { get; set; }
        public string? Missing { get; set; } = "configured";
        public NestedConfiguration Nested { get; set; } = new();
    }

    public sealed class NestedConfiguration
    {
        public string Label { get; set; } = "";
        public int[] Numbers { get; set; } = [];
        public bool Enabled { get; set; } = true;
    }

    public sealed class FloatingConfiguration
    {
        public double Small { get; set; }
        public double Subnormal { get; set; }
        public double Rounded { get; set; }
    }
}
