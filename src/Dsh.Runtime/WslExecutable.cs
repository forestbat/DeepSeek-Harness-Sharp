namespace Dsh.Runtime;

/** Locates Windows's inbox `wsl.exe` entrypoint consistently across tools and fleet transport. */
public static class WslExecutable
{
    public const string ExecutableName = "wsl.exe";
    public const string Utf8MessagesName = "WSL_UTF8";
    public const string Enabled = "1";

    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, ExecutableName);

    public static bool IsAvailable(string? executablePath = null)
        => File.Exists(Resolve(executablePath));

    public static string Resolve(string? executablePath = null)
        => string.IsNullOrWhiteSpace(executablePath) ? DefaultPath : executablePath;

    public static string Require(string? executablePath = null, string? guidanceSuffix = null)
    {
        var path = Resolve(executablePath);
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"WSL is not installed ({path} not found); install it with `wsl.exe --install` from an elevated prompt{guidanceSuffix ?? "."}");
    }
}
