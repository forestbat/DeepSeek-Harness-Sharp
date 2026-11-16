using Dsh.Ma2a.Integration;

if (args.Length is 6 or 8 && args[0] == "--cross-state" && args[2] == "--target-file" && args[4] == "--root")
{
    try
    {
        using var target = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[3]));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        var platform = args.Length == 8 && args[6] == "--platform" ? args[7] : "linux";
        await CrossMachineScenarios.RunAsync(args[1], target.RootElement.GetProperty("endpoint_id").GetString()!, args[5], cancellation.Token, platform);
        return 0;
    }
    catch (Exception error)
    {
        await Console.Error.WriteLineAsync($"FAIL {error.GetType().Name}: {error.Message}");
        return 1;
    }
}

if (args.Length != 4 || args[0] != "--ma2a" || args[2] != "--state-root")
{
    await Console.Error.WriteLineAsync("Usage: Dsh.Ma2a.Integration --ma2a <absolute-binary> --state-root <repo-local-directory>");
    return 2;
}

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    await using var pair = await NativeRuntimePair.StartAsync(args[1], args[3], cancellation.Token);
    await ControlScenarios.InitializeAsync(pair, cancellation.Token);
    await ServicesScenarios.RunAsync(pair, cancellation.Token);
    await GuiScenarios.RunAsync(pair, cancellation.Token);
    await ToolScenarios.RunAsync(pair, cancellation.Token);
    await ControlScenarios.RevokeAndRestartAsync(pair, cancellation.Token);
    await NativeRuntimePair.PassedAsync("All native MA2A Runtime scenarios passed.");
    return 0;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync($"FAIL {error.GetType().Name}: {error.Message}");
    return 1;
}
