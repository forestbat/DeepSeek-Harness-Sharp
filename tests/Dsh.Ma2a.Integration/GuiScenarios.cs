using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.Terminal;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Ma2a.Integration;

internal static class GuiScenarios
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(10);
    private const string TerminalMarker = "ma2a-gui-native-consumer";
    private const string StaleMarker = "acceptance-stale-projection";

    public static async Task RunAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Ma2aGuiAppBuilder), AvaloniaTestIsolationLevel.PerTest);
        await session.Dispatch(async () =>
        {
            await RunConsoleAsync(pair, cancellationToken);
            return true;
        }, cancellationToken);
        await pair.Caller.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
        await pair.Target.CallAsync(NativeRuntimePair.Command("status"), cancellationToken);
        await NativeRuntimePair.PassedAsync("Disposing real GUI views leaves both task-owned MA2A Runtimes running.");
    }

    private static async Task RunConsoleAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        using var caller = new Ma2aViewModel { StateDirectory = pair.CallerStateDirectory, Executable = pair.BinaryPath };
        using var target = new Ma2aViewModel { StateDirectory = pair.TargetStateDirectory, Executable = pair.BinaryPath };
        using var canceled = cancellationToken.Register(() => { caller.Dispose(); target.Dispose(); });
        var view = new Ma2aView { DataContext = caller };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await caller.ConnectCommand.ExecuteAsync(null);
            await target.ConnectCommand.ExecuteAsync(null);
            NativeRuntimePair.Check(caller.Connected && caller.Endpoint == pair.CallerId, "The GUI must attach to the real caller identity.");
            NativeRuntimePair.Check(target.Connected && target.Endpoint == pair.TargetId, "The operator GUI must attach to the real target identity.");
            NativeRuntimePair.Check(Labels(caller.Snapshot).Contains($"space id: {pair.SpaceId}"), "The caller GUI must show its signed Space membership.");
            await InspectManagementAsync(target, pair, cancellationToken);
            await NativeRuntimePair.PassedAsync("Real MA2A GUI attaches, renders Endpoint/Space snapshots, members, grants and local exposures.");
            await FilesAsync(caller.Remote, pair, cancellationToken);
            await TransfersAsync(caller.Remote, pair, cancellationToken);
            await ExecAsync(view, window, caller.Remote, pair, cancellationToken);
            await RestartAsync(caller, pair, cancellationToken);
        }
        finally { window.Close(); }
    }

    private static async Task InspectManagementAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectManagement(model, "space_details_fetch");
        model.Fields.Single(field => field.Name == "space_id").Text = pair.SpaceId;
        await model.ExecuteCommand.ExecuteAsync(null);
        cancellationToken.ThrowIfCancellationRequested();
        var members = Labels(model.Result);
        NativeRuntimePair.Check(members.Contains($"endpoint id: {pair.CallerId}"), "Space details in the GUI must include the actual enrolled caller.");
        NativeRuntimePair.Check(members.Contains("files read: True"), "The GUI must render the signed Files read grant independently of connectivity.");
        SelectManagement(model, "service_exposure_list");
        await model.ExecuteCommand.ExecuteAsync(null);
        var exposures = Labels(model.Result);
        NativeRuntimePair.Check(exposures.Contains("root: data") && exposures.Contains("access: read_write"), "The target GUI must render the exposed root and its write policy.");
        NativeRuntimePair.Check(!model.HasPending, "Read-only GUI inspection must not create an indeterminate mutation.");
    }

    private static async Task FilesAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes("GUI native Files consumer: 文件\n");
        remote.TargetEndpoint = pair.TargetId;
        SelectRemote(remote, "files", "write");
        Text(remote, "path", "data:/gui-native.txt");
        Text(remote, "content_hex", Convert.ToHexStringLower(bytes));
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("Outcome"), "GUI native Files write must reach a terminal response.");
        SelectRemote(remote, "files", "list");
        Text(remote, "path", "data:/");
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(Labels(remote.Results).Any(label => label.Contains("gui-native.txt", StringComparison.Ordinal)), "GUI Files listing must contain the file created through its native write action.");
        var captured = Path.Combine(pair.Root, "gui-files-read.bin");
        SelectRemote(remote, "files", "read");
        Text(remote, "path", "data:/gui-native.txt");
        remote.RawOutputPath = captured;
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending, "GUI Files read must not leave an indeterminate result.");
        NativeRuntimePair.Check((await File.ReadAllBytesAsync(captured, cancellationToken)).SequenceEqual(bytes), "The GUI's raw output sink must preserve actual Files bytes, including UTF-8 content.");
        remote.RawOutputPath = "";
        await NativeRuntimePair.PassedAsync("GUI native Files write/list/read uses the real remote root and preserves exact output bytes.");
    }

    private static async Task TransfersAsync(Ma2aRemoteViewModel remote, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var source = Path.Combine(pair.Root, "gui-transfer-source.bin");
        var destination = Path.Combine(pair.Root, "gui-transfer-result.bin");
        byte[] content = [.. Encoding.UTF8.GetBytes("GUI transfer integrity consumer\n"), 0, 255, 128];
        await File.WriteAllBytesAsync(source, content, cancellationToken);
        SelectRemote(remote, "transfer", "upload");
        Text(remote, "path", "data:/gui-transfer.bin");
        Text(remote, "local_path", source);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("verified transfer"), "GUI upload must complete through the verified transfer driver.");
        SelectRemote(remote, "transfer", "download");
        Text(remote, "path", "data:/gui-transfer.bin");
        Text(remote, "local_path", destination);
        await RunRemoteAsync(remote, cancellationToken);
        NativeRuntimePair.Check(!remote.HasPending && Labels(remote.Results).Contains("verified transfer"), "GUI download must expose the verified transfer result.");
        NativeRuntimePair.Check((await File.ReadAllBytesAsync(destination, cancellationToken)).SequenceEqual(content), "GUI transfer must preserve both text and non-text bytes.");
        await NativeRuntimePair.PassedAsync("GUI upload/download uses real MA2A transfer streams and BLAKE3 verification.");
    }

    private static async Task ExecAsync(Ma2aView view, Window window, Ma2aRemoteViewModel remote,
        NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        SelectRemote(remote, "exec", "exec");
        Text(remote, "program", OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
        var args = remote.Fields.Single(field => field.Name == "args");
        AddArgument(args, OperatingSystem.IsWindows() ? "/c" : "-c");
        AddArgument(args, OperatingSystem.IsWindows() ? $"echo {TerminalMarker}" : $"printf {TerminalMarker}");
        view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        await RunRemoteAsync(remote, cancellationToken);
        await UntilAsync(() => !remote.Busy && ScreenContains(remote.Terminal, TerminalMarker), "The real GUI VT terminal must render native Exec stdout and reach its terminal outcome.", cancellationToken);
        Dispatcher.UIThread.RunJobs();
        var terminal = view.GetVisualDescendants().OfType<TerminalControl>().Single();
        NativeRuntimePair.Check(ReferenceEquals(terminal.Session, remote.Terminal), "The rendered TerminalControl must consume the native MA2A terminal session.");
        NativeRuntimePair.Check(Labels(remote.Results).Contains("terminal"), "The GUI must retain native terminal admission/outcome metadata.");
        using var image = window.CaptureRenderedFrame();
        NativeRuntimePair.Check(image is not null, "The native GUI acceptance view must produce a rendered frame.");
        image!.Save(Path.Combine(pair.Root, "gui-native-terminal.png"));
        await NativeRuntimePair.PassedAsync("Real Exec stdout reaches the existing GUI VT terminal and a rendered MA2A view.");
    }

    private static async Task RestartAsync(Ma2aViewModel model, NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var before = await pair.Caller.GetSnapshotAsync(cancellationToken);
        var boot = before.GetProperty("runtime_boot_id").GetString();
        model.Snapshot.Add(new Ma2aResultNode(StaleMarker, []));
        await pair.RestartCallerAsync(cancellationToken);
        await model.RefreshCommand.ExecuteAsync(null);
        await UntilAsync(() => model.Connected && !model.Snapshot.Any(node => node.Label == StaleMarker), "A Runtime boot change must invalidate and replace the GUI projection.", cancellationToken);
        var after = await pair.Caller.GetSnapshotAsync(cancellationToken);
        NativeRuntimePair.Check(after.GetProperty("runtime_boot_id").GetString() != boot, "The tested GUI refresh must span an actual Runtime restart.");
        NativeRuntimePair.Check(model.Endpoint == pair.CallerId, "GUI resynchronization must retain the persistent Endpoint identity.");
        NativeRuntimePair.Check(Labels(model.Snapshot).Contains($"space id: {pair.SpaceId}"), "GUI resynchronization must restore complete Space membership.");
        await NativeRuntimePair.PassedAsync("GUI resynchronizes after real Runtime restart and retains the persistent Endpoint identity.");
    }

    private static void SelectManagement(Ma2aViewModel model, string operation)
    {
        var command = Ma2aCommandCatalog.Commands.Single(command => command.Operation == operation);
        model.SelectedGroup = command.Group;
        model.SelectedCommand = command;
    }

    private static void SelectRemote(Ma2aRemoteViewModel model, string family, string operation)
        => model.SelectedOperation = model.Operations.Single(item => item.Family == family && item.Name == operation);

    private static void Text(Ma2aRemoteViewModel model, string name, string value)
        => model.Fields.Single(field => field.Name == name).Text = value;

    private static void AddArgument(Ma2aFieldViewModel field, string value)
    {
        field.AddItemCommand.Execute(null);
        field.Items[^1].Text = value;
    }

    private static async Task RunRemoteAsync(Ma2aRemoteViewModel model, CancellationToken cancellationToken)
    {
        model.Confirmed = true;
        await model.RunCommand.ExecuteAsync(null).WaitAsync(cancellationToken);
    }

    private static IReadOnlyList<string> Labels(IEnumerable<Ma2aResultNode> nodes)
        => nodes.SelectMany(node => new[] { node.Label }.Concat(Labels(node.Children))).ToArray();

    private static bool ScreenContains(TerminalSession? terminal, string value)
    {
        if (terminal is null) return false;
        lock (terminal.Gate) return terminal.Screen.Snapshot().Contains(value, StringComparison.Ordinal);
    }

    private static async Task UntilAsync(Func<bool> condition, string failure, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            NativeRuntimePair.Check(elapsed.Elapsed < Arrival, failure);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }
}

public static class Ma2aGuiAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Dsh.Gui.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
}
