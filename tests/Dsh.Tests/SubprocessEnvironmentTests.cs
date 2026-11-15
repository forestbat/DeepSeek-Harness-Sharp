using Dsh.Runtime;
using Dsh.Subprocess;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class SubprocessEnvironmentTests
{
    private const int OutputLimitBytes = 1024;

    [Theory]
    [InlineData("DshAuditApiKey", false)]
    [InlineData("DshAuditPassword", false)]
    [InlineData("DshAuditSecret", false)]
    [InlineData("DshAuditToken", false)]
    [InlineData("DSH_AUDIT_CHILD_VALUE", false)]
    [InlineData("dsh_audit_child_value", false)]
    [InlineData("DshAuditPublicValue", true)]
    public async Task Spawn_FiltersInheritedEnvironment(string variable, bool inherited)
    {
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "test-input");

            var presence = await ReadVariablePresenceAsync(variable);

            Assert.Equal(inherited ? "present" : "absent", presence);
            Assert.Equal("test-input", Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Theory]
    [InlineData("DSH_AUDIT_EXPLICIT_VALUE", "test-input", true)]
    [InlineData("DshAuditRemovedValue", null, false)]
    public async Task Spawn_AppliesExplicitOverridesAfterFiltering(string variable, string? value, bool present)
    {
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "test-input");

            var presence = await ReadVariablePresenceAsync(variable, new Dictionary<string, string?> { [variable] = value });

            Assert.Equal(present ? "present" : "absent", presence);
            Assert.Equal("test-input", Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    private static async Task<string> ReadVariablePresenceAsync(string variable, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        using var service = new SubprocessService(new Context());
        using var child = await service.SpawnAsync(new SubprocessSpawnSpec
        {
            Argv = OperatingSystem.IsWindows()
                ? ["cmd.exe", "/d", "/c", $"if defined {variable} (echo present) else (echo absent)"]
                : ["/bin/sh", "-c", $$"""if [ "${{{variable}}+x}" ]; then printf present; else printf absent; fi"""],
            Cwd = TempTree.RepositoryRoot,
            Env = overrides,
            Stdout = new SubprocessCollect(OutputLimitBytes, null),
            Stderr = new SubprocessCollect(OutputLimitBytes, null),
            Signal = TestContext.Current.CancellationToken,
        });
        var outcome = await child.Done.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, outcome.ExitCode);
        return child.StdoutReader.ReadFrom(0).Text.Trim();
    }
}
