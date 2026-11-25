using Dsh.Pty;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Tests;

/**
 * pty 落盘记录的往返与复活前提:
 * 记录决定"同一个 pty id 能不能被重新拉起", 所以它必须能容忍半写/损坏, 且不会把坏记录当成可用的启动参数。
 */
public sealed class PtyRecordTests
{
    [Fact(Timeout = 20000)]
    public async Task Records_WorkWithoutJsonReflection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateDirectory("pty-records-json");
        Process? child = null;
        try
        {
            var assembly = typeof(PtyRecordTests).Assembly.Location;
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(assembly, ".runtimeconfig.json"), cancellationToken))!;
            configuration["runtimeOptions"]!["configProperties"]!["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = false;
            var configurationPath = Path.Combine(root, "records.runtimeconfig.json");
            await File.WriteAllTextAsync(configurationPath, configuration.ToJsonString(), cancellationToken);
            var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var start = new ProcessStartInfo(dotnet, ["exec", "--runtimeconfig", configurationPath, assembly,
                "-method", $"{typeof(PtyRecordTests).FullName}.{nameof(Record_WithoutJsonReflection_SurvivesRoundTripAndListsById)}", "-explicit", "on", "-parallelMode", "none"])
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            child = TestProcessScope.Start(start);
            var output = child.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = child.StandardError.ReadToEndAsync(cancellationToken);
            await child.WaitForExitAsync(cancellationToken);
            Assert.True(child.ExitCode == 0, $"{await output}\n{await error}");
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
            child?.Dispose();
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public void Record_WithoutJsonReflection_SurvivesRoundTripAndListsById()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Record_SurvivesRoundTripAndListsById();
    }

    [Fact]
    public void Record_SurvivesRoundTripAndListsById()
    {
        var root = TempTree.CreateDirectory("pty-records");
        try
        {
            var record = new PtyRecord
            {
                Id = "pty-abc",
                FileName = "/bin/sh",
                Arguments = ["-c", "dsharp tui"],
                WorkingDirectory = "/tmp/work",
                Environment = new Dictionary<string, string?> { ["FOO"] = "bar" },
                Home = "/tmp/home",
                Rows = 40,
                Columns = 120,
                WantsMouse = true,
                AgentSessionId = "session-1",
                SizeGeneration = 7,
            };
            PtyRecords.Save(record, root);

            var loaded = PtyRecords.TryLoad("pty-abc", root);
            Assert.NotNull(loaded);
            Assert.Equal("/bin/sh", loaded.FileName);
            Assert.Equal(["-c", "dsharp tui"], loaded.Arguments);
            Assert.Equal("/tmp/work", loaded.WorkingDirectory);
            Assert.Equal("/tmp/home", loaded.Home);
            Assert.Equal(40, loaded.Rows);
            Assert.Equal(120, loaded.Columns);
            Assert.True(loaded.WantsMouse);
            Assert.Equal("session-1", loaded.AgentSessionId);
            Assert.Equal(7, loaded.SizeGeneration);
            Assert.Equal("bar", loaded.Environment!["FOO"]);

            Assert.Equal("pty-abc", Assert.Single(PtyRecords.List(root)).Id);
            PtyRecords.Delete("pty-abc", root);
            Assert.Null(PtyRecords.TryLoad("pty-abc", root));
            Assert.Empty(PtyRecords.List(root));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void CorruptOrEmptyRecord_IsIgnoredInsteadOfThrowing()
    {
        var root = TempTree.CreateDirectory("pty-records-corrupt");
        try
        {
            var directory = PtyRecords.DirectoryOf(root);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pty-broken.json"), "{not json");
            File.WriteAllText(Path.Combine(directory, "pty-empty.json"), "{\"id\":\"pty-empty\"}");

            Assert.Null(PtyRecords.TryLoad("pty-broken", root));
            Assert.Null(PtyRecords.TryLoad("pty-empty", root));
            Assert.Empty(PtyRecords.List(root));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Start_ReplacesAnExitedSessionWithTheSameId()
    {
        var id = $"pty-test-{Guid.NewGuid():N}";
        using var host = new PtyHost();
        var first = await host.StartAsync(ExitInfo(), id, TestContext.Current.CancellationToken);
        await WaitForExit(first);
        Assert.Equal(PtySessionStatus.Exited, host.List().Single().Status);

        // 同 id 复活: 已退出的那一份被替换, 而不是抛 "already exists"。
        var revived = await host.StartAsync(ExitInfo(), id, TestContext.Current.CancellationToken);
        Assert.Same(revived, host.Get(id));
        await WaitForExit(revived);
    }

    private static PtyStartInfo ExitInfo() => OperatingSystem.IsWindows()
        ? new PtyStartInfo { FileName = "cmd.exe", Arguments = ["/c", "exit 0"] }
        : new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", "exit 0"] };

    private static async Task WaitForExit(PtySession session)
    {
        for (var attempt = 0; attempt < 200 && session.Status == PtySessionStatus.Running; attempt += 1)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(PtySessionStatus.Exited, session.Status);
    }
}
