using System.Diagnostics;
using System.Text;
using Dsh.Runtime;

namespace Dsh.Fleet;

/** Local Windows WSL transport executing `/bin/sh -c` and streaming uploads via `wsl.exe --cd ~ --exec`. */
public sealed class WslTransport(MachineSpec machine, string? executablePath = null) : IRemoteMachineTransport
{
    private static readonly TimeSpan ListDistrosTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(10);
    private const int UploadBufferSize = 81920;

    private readonly string? _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;

    public MachineSpec Machine => machine;

    public static bool IsAvailable(string? executablePath = null)
        => WslExecutable.IsAvailable(executablePath);

    public static string ResolveExecutable(string? executablePath = null)
        => WslExecutable.Require(executablePath);

    public static async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken cancellationToken, string? executablePath = null)
    {
        var startInfo = NewStartInfo(ResolveExecutable(executablePath));
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add("--quiet");
        var result = NormalizeFailure(await SshTransport.CaptureProcessAsync(startInfo, cancellationToken, timeout: ListDistrosTimeout).ConfigureAwait(false));
        if (result.ExitCode != 0)
            throw new IOException($"wsl --list 失败: {result.FailureDetail}");
        return DecodeListOutput(result.StdOut);
    }

    internal static IRemoteMachineTransport Create(MachineSpec machine, FleetOptions options) => machine.Kind switch
    {
        MachineKinds.Wsl => new WslTransport(machine, options.WslExecutablePath),
        MachineKinds.Ssh or MachineKinds.Iroh => new SshTransport(machine, options.SshTransportAdapter),
        _ => throw new NotSupportedException($"No remote transport is supported for machine kind \"{machine.Kind}\"."),
    };

    public static RemoteDsharpInstaller CreateInstaller(MachineSpec machine, FleetOptions options)
        => new(Create(machine, options), machine.IsWsl ? null : options.SshTransportAdapter);

    internal SshCommandChannel OpenCommand(string command)
    {
        var startInfo = BuildStartInfo(command);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start wsl.exe");
        var stderr = new StringBuilder();
        _ = SshTransport.DrainAsync(process.StandardError, stderr);
        return new SshCommandChannel(process, askPass: null, () => SshTransport.Describe(stderr));
    }

    public async Task<RemoteProcessResult> CaptureAsync(string command, CancellationToken cancellationToken)
        => NormalizeFailure(await SshTransport.CaptureProcessAsync(BuildStartInfo(command), cancellationToken, timeout: DefaultCommandTimeout).ConfigureAwait(false));

    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(command, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.StdOut
            : throw new IOException($"{machine.Target} wsl 命令失败: {result.FailureDetail}");
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo($"mkdir -p \"$(dirname {RemoteShell.Quote(remotePath)})\" && cat > {RemoteShell.Quote(remotePath)}");
        var result = NormalizeFailure(await SshTransport.CaptureProcessAsync(
            startInfo,
            cancellationToken,
            (destination, token) => CopyLocalFileAsync(localPath, destination, token),
            DefaultCommandTimeout).ConfigureAwait(false));
        if (result.ExitCode != 0)
            throw new IOException($"{machine.Target} wsl 上传失败: {result.FailureDetail}");
    }

    public string CommandLine(string command)
        => string.Join(' ', BuildArguments(command).Prepend(WslExecutable.ExecutableName).Select(QuoteWindowsArgument));

    private ProcessStartInfo BuildStartInfo(string command)
    {
        machine.ValidateTransportSettings();
        var startInfo = NewStartInfo(ResolveExecutable(_executablePath));
        foreach (var argument in BuildArguments(command))
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private string[] BuildArguments(string command)
    {
        List<string> arguments = [];
        if (!string.IsNullOrWhiteSpace(machine.WslDistro))
        {
            arguments.Add("-d");
            arguments.Add(machine.WslDistro);
        }
        if (!string.IsNullOrWhiteSpace(machine.User))
        {
            arguments.Add("-u");
            arguments.Add(machine.User);
        }
        arguments.Add("--cd");
        arguments.Add("~");
        arguments.Add("--exec");
        arguments.Add("/bin/sh");
        arguments.Add("-c");
        arguments.Add(command);
        return [.. arguments];
    }

    private static ProcessStartInfo NewStartInfo(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment[WslExecutable.Utf8MessagesName] = WslExecutable.Enabled;
        return startInfo;
    }

    private static async Task CopyLocalFileAsync(string localPath, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, UploadBufferSize, useAsync: true);
        await source.CopyToAsync(destination, UploadBufferSize, cancellationToken).ConfigureAwait(false);
    }

    /** `wsl.exe` itself often writes service errors to stdout and exits with an HRESULT/negative status while stderr stays empty. */
    private static RemoteProcessResult NormalizeFailure(RemoteProcessResult result)
    {
        var transportFailed = result.ExitCode is < 0 or > 255;
        var stdErr = result.StdErr;
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(stdErr) && !string.IsNullOrWhiteSpace(result.StdOut))
            stdErr = result.StdOut;
        return new RemoteProcessResult(result.ExitCode, result.StdOut, stdErr, transportFailed);
    }

    /** Quotes Windows command-line arguments so CommandLine strings stay copy-pastable in cmd.exe and PowerShell. */
    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length != 0 && !value.Any(static character => char.IsWhiteSpace(character) || character == '"'))
            return value;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /** `wsl.exe --list --quiet` historically writes UTF-16LE with a BOM even when read as UTF-8, leaving NUL/BOM bytes between characters. */
    private static string[] DecodeListOutput(string output)
    {
        var normalized = output.Replace("\0", "", StringComparison.Ordinal).Replace("\uFEFF", "", StringComparison.Ordinal);
        return [.. normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)];
    }
}
