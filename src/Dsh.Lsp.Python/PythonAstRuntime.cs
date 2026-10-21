using System.Diagnostics;
using System.Text.Json;
using Dsh.Lsp.Client;

namespace Dsh.Lsp.Python;

internal static class PythonAstRuntime
{
    public static async Task<JsonDocument?> RunAsync(
        string script,
        LspDocument document,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        using var process = new Process();
        process.StartInfo = startInfo;
        try
        {
            if (!process.Start())
                return null;
            var input = JsonSerializer.Serialize(new
            {
                source = document.Text,
                language = document.LanguageId,
                payload,
            });
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;
            return JsonDocument.Parse(output);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
