using System.Globalization;
using Dsh.Transport;

namespace Dsh.CSharpRepl;

/** Preserves the frontend's managed exit code before Unix truncates it to eight bits. */
internal sealed class CSharpReplExitStatus : IDisposable
{
    private readonly string _directory;
    private readonly string _statusPath;

    public CSharpReplExitStatus()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"dsh-csharprepl-{Guid.NewGuid():N}");
        _statusPath = Path.Combine(_directory, "exit-status");
        PreloadPath = Path.Combine(_directory, "preload.csx");
        OwnerOnlyFiles.EnsureDirectory(_directory);
        try
        {
            OwnerOnlyFiles.Write(_statusPath, "");
            var literal = $"@\"{_statusPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            // The documented startup script runs before collected stdin without rewriting the submitted program.
            // https://github.com/waf/CSharpRepl/blob/v0.9.2/CSharpRepl/Repls/PipedInputEvaluator.cs
            OwnerOnlyFiles.Write(PreloadPath, $$"""
                // Persist the final managed exit code synchronously before process shutdown completes.
                System.AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    System.IO.File.WriteAllText({{literal}}, System.Environment.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                """);
        }
        catch
        {
            Directory.Delete(_directory, recursive: true);
            throw;
        }
    }

    public string PreloadPath { get; }

    public async Task<int> ReadAsync(CancellationToken signal)
    {
        var text = await File.ReadAllTextAsync(_statusPath, signal);
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
            throw new FormatException("CSharpRepl did not report a complete managed exit status through its startup script");
        return code;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
