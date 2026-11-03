namespace Dsh.Tests;

/** Actual file operations inside the owned child, independent of command-interpreter parsing. */
internal static class SandboxFileGrantConsumer
{
    internal const string Argument = "--sandbox-file-grant-consumer";
    internal const string Input = "native-pipe";
    internal const string Readable = "readable\r\nsecond-readable-line\r\nlast-readable-line\r\n";
    private const int AccessDenied = unchecked((int)0x80070005);

    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            Assert.Equal(5, arguments.Length);
            Assert.Equal(Input, await Console.In.ReadLineAsync());
            await Console.Error.WriteLineAsync("stage=read");
            Assert.Equal(Readable, await File.ReadAllTextAsync(arguments[1]));
            await Console.Out.WriteLineAsync("read-complete:readable/second-readable-line/last-readable-line");
            await VerifyDeniedPathsAsync(arguments[1], arguments[2], arguments[3], bool.Parse(arguments[4]));
            await VerifyWritableDirectoryAsync();
            await Console.Out.WriteLineAsync(Input);
            await Console.Error.WriteLineAsync("diagnostic");
            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync($"file-consumer error={error.GetType().Name}; hresult=0x{unchecked((uint)error.HResult):x8}");
            return 1;
        }
    }

    private static async Task VerifyDeniedPathsAsync(string readable, string secret, string settings, bool protectedParent)
    {
        await RequireDeniedAsync("parent-enumeration", () => { _ = Directory.GetFileSystemEntries(Path.GetDirectoryName(readable)!); return Task.CompletedTask; });
        await RequireDeniedAsync("secret-read", async () => { _ = await File.ReadAllTextAsync(secret); });
        await RequireDeniedAsync("input-write", () => File.AppendAllTextAsync(readable, "forbidden"));
        await RequireDeniedAsync("settings-write", () => File.AppendAllTextAsync(settings, "forbidden"));
        await RequireDeniedAsync("settings-rename", () => { File.Move(settings, Path.Combine(Path.GetDirectoryName(settings)!, "moved.yaml")); return Task.CompletedTask; });
        await RequireDeniedAsync("settings-delete", () => { File.Delete(settings); return Task.CompletedTask; });
        if (protectedParent)
            await RequireDeniedAsync("settings-parent-rename", () => { Directory.Move(Path.GetDirectoryName(settings)!, Path.Combine(Directory.GetCurrentDirectory(), "moved-config")); return Task.CompletedTask; });
    }

    private static async Task VerifyWritableDirectoryAsync()
    {
        await Console.Error.WriteLineAsync("stage=create");
        await File.WriteAllTextAsync("allowed.txt", "allowed");
        await File.WriteAllTextAsync("rename-before.txt", "disposable");
        Assert.Equal("allowed", await File.ReadAllTextAsync("allowed.txt"));
        await Console.Error.WriteLineAsync("stage=rename");
        File.Move("rename-before.txt", "rename-after.txt");
        Assert.False(File.Exists("rename-before.txt"));
        Assert.Equal("disposable", await File.ReadAllTextAsync("rename-after.txt"));
        await Console.Error.WriteLineAsync("stage=delete");
        File.Delete("rename-after.txt");
        Assert.False(File.Exists("rename-after.txt"));
    }

    private static async Task RequireDeniedAsync(string operation, Func<Task> action)
    {
        await Console.Error.WriteLineAsync($"stage={operation}");
        try { await action(); }
        catch (UnauthorizedAccessException error)
        {
            await Console.Error.WriteLineAsync($"denied={operation}; hresult=0x{unchecked((uint)error.HResult):x8}");
            return;
        }
        catch (IOException error) when (error.HResult == AccessDenied)
        {
            await Console.Error.WriteLineAsync($"denied={operation}; hresult=0x{unchecked((uint)error.HResult):x8}");
            return;
        }
        throw new InvalidOperationException($"The protected operation unexpectedly succeeded: {operation}.");
    }
}
