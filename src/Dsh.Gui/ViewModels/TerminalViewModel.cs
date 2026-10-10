using System.IO.Pipelines;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/** GUI 终端: 本地 PTY 与远端 PTY 共用同一套落屏(VtScreen)与输入发送, 只换底层通道(§14 C)。 */
public sealed partial class TerminalViewModel : ObservableObject, IAsyncDisposable
{
    private const int Columns = 100;
    private const int Rows = 30;

    private readonly VtScreen _screen = new(Columns, Rows);
    private readonly CancellationTokenSource _cts = new();
    private Stream? _remoteInput;
    private PtySession? _local;
    private IRemoteHost? _remote;
    private string? _ptyId;
    private Task? _pump;

    public TerminalViewModel(string title) => Title = title;

    public string Title { get; }

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private string _status = "正在启动…";

    /** 本地终端: 进程内 PTY。 */
    public async Task StartLocalAsync(string homeRoot)
    {
        var shell = PtyShell.Resolve();
        _local = await PtyHost.Default.StartAsync(
            new PtyStartInfo
            {
                FileName = shell,
                Arguments = [.. PtyShell.Arguments(shell)],
                Home = homeRoot,
                Rows = Rows,
                Columns = Columns,
            },
            cancellationToken: _cts.Token);
        Status = $"本地 · {shell}";
        _pump = PumpLocalAsync(_cts.Token);
    }

    /** 远端终端: 经 RPC 起 PTY, 本地只搬运字节。 */
    public async Task StartRemoteAsync(IRemoteHost host, string platform)
    {
        _remote = host;
        var (fileName, arguments) = platform == "windows"
            ? ("cmd.exe", (string[])[])
            : ("/bin/sh", (string[])["-l"]);
        _ptyId = await host.StartPtyAsync(fileName, [.. arguments], _cts.Token);
        var inputPipe = new Pipe();
        var outputPipe = new Pipe();
        _remoteInput = inputPipe.Writer.AsStream();
        _ = host.AttachPtyAsync(_ptyId, inputPipe.Reader.AsStream(), outputPipe.Writer.AsStream(), _cts.Token);
        Status = $"远端 · {fileName}";
        _pump = PumpRemoteAsync(outputPipe.Reader, _cts.Token);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(Input + "\n");
        Input = "";
        try
        {
            if (_local is not null)
                await _local.WriteAsync(bytes, _cts.Token);
            else if (_remoteInput is not null)
                await _remoteInput.WriteAsync(bytes, _cts.Token);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task PumpLocalAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _local!.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;
                Feed(buffer.AsSpan(0, read), cancellationToken);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
    }

    private async Task PumpRemoteAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken);
                foreach (var segment in result.Buffer)
                    Feed(segment.Span, cancellationToken);
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
    }

    private void Feed(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        _screen.Feed(bytes);
        var snapshot = Render();
        Dispatcher.UIThread.Post(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
                Text = snapshot;
        });
    }

    private string Render()
    {
        var builder = new StringBuilder(Rows * (Columns + 1));
        var chars = new char[Columns];
        for (var y = 0; y < Rows; y++)
        {
            var row = _screen.Row(y);
            for (var x = 0; x < Columns; x++)
                chars[x] = TerminalSafeGlyphs.AsciiSafe(row[x].Character);
            builder.Append(chars).Append('\n');
        }
        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_pump is not null)
                await _pump;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
        if (_local is not null)
            await _local.StopAsync();
        if (_remote is not null && _ptyId is not null)
        {
            try
            {
                await _remote.StopPtyAsync(_ptyId, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
            }
        }
        _cts.Dispose();
    }
}
