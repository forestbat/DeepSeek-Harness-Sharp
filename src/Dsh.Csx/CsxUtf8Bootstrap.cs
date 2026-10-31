using Dsh.Transport;

namespace Dsh.Csx;

/** Initializes redirected Windows script output before the original, unmodified script executes. */
internal sealed class CsxUtf8Bootstrap : IDisposable
{
    private readonly string _directory;

    public CsxUtf8Bootstrap(string script, string workdir)
    {
        _directory = Path.Combine(workdir, $".dsh-csx-{Guid.NewGuid():N}");
        ScriptPath = Path.Combine(_directory, "entry.csx");
        OwnerOnlyFiles.EnsureDirectory(_directory);
        try
        {
            OwnerOnlyFiles.Write(Path.Combine(_directory, "encoding.csx"), "global::System.Console.OutputEncoding = new global::System.Text.UTF8Encoding(false);\n");
            // Use native #load so Roslyn retains the original paths for nested #load, #r and diagnostics.
            // Windows paths cannot contain quotes; forward slashes also avoid directive escape sequences.
            OwnerOnlyFiles.Write(ScriptPath, $$"""
                #load "encoding.csx"
                #load "{{script.Replace('\\', '/')}}"
                """);
        }
        catch
        {
            Directory.Delete(_directory, recursive: true);
            throw;
        }
    }

    public string ScriptPath { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
