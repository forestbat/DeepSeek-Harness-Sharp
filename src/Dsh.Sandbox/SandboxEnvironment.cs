using System.Text;

namespace Dsh.Sandbox;

internal static class SandboxEnvironment
{
    internal static readonly HashSet<string> PrivateDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TMPDIR", "TMP", "TEMP", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR"
    };
    private static readonly HashSet<string> LoaderVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS", "IFS", "GCONV_PATH", "LOCPATH", "PYTHONPATH", "PYTHONHOME", "PERL5OPT", "PERL5LIB", "RUBYOPT", "RUBYLIB", "NODE_OPTIONS", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE"
    };
    private static readonly string[] TrustedLauncherEntries = CreateBaseline().Select(static pair => $"{pair.Key}={pair.Value}").ToArray();

    public static string[] LauncherEntries => TrustedLauncherEntries;

    private static Dictionary<string, string> CreateBaseline()
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            values["SystemRoot"] = windows;
            values["WINDIR"] = windows;
            // Windows needs this source folder to redirect the AppContainer's private profile during process creation.
            values["LOCALAPPDATA"] = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            values["PATH"] = "/usr/bin:/bin";
            values["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
            values["HOME"] = "/nonexistent";
            values["TMPDIR"] = "/tmp";
        }
        values["DOTNET_EnableDiagnostics"] = "0";
        return values;
    }

    public static string[] Create(IReadOnlyDictionary<string, string?> requested, SandboxPath[] writable)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Count > 128)
            throw new ArgumentException("The explicit sandbox environment is limited to 128 entries.");
        var values = CreateBaseline();
        var bytes = 0L;
        foreach (var (key, value) in requested)
        {
            if (key is null || key.Length is 0 or > 128 || (!char.IsAsciiLetter(key[0]) && key[0] != '_') || key.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new ArgumentException("Sandbox environment names must be nonempty ASCII identifiers of at most 128 characters.");
            if (value?.Contains('\0') == true)
                throw new ArgumentException("Sandbox environment values cannot contain NUL.");
            bytes += Encoding.UTF8.GetByteCount(key) + (value is null ? 0 : Encoding.UTF8.GetByteCount(value)) + 2;
            if (bytes > 64 * 1024)
                throw new ArgumentException("The explicit sandbox environment exceeds 64 KiB of UTF-8 data.");
            if (IsLoaderInjection(key))
                throw new ArgumentException("Loader and shell startup injection variables cannot be forwarded to sandboxes.");
            if (value is null)
            {
                values.Remove(key);
                continue;
            }
            if (PrivateDirectoryNames.Contains(key))
                ValidatePrivateDirectory(value, writable);
            values[key] = value;
        }
        var result = values.OrderBy(static item => item.Key, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(static item => $"{item.Key}={item.Value}").ToArray();
        if (OperatingSystem.IsWindows() && result.Sum(static value => (long)value.Length + 1) + 1 > 32767)
            throw new ArgumentException("The Windows sandbox environment exceeds the native UTF-16 block limit.");
        return result;
    }

    private static void ValidatePrivateDirectory(string path, SandboxPath[] writable)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ArgumentException("Sandbox HOME, profile, cache, and temporary directories must already exist at absolute private paths.");
        var canonical = SandboxPolicy.CanonicalExistingPath(path);
        if (SandboxHostPaths.ExposesHome(canonical) || SandboxHostPaths.IsCredentialPath(canonical) || !writable.Any(grant => grant.IsDirectory && SandboxPolicy.Contains(grant.Source, canonical)))
            throw new ArgumentException("Sandbox HOME, profile, cache, and temporary directories must be inside explicit writable grants, never the ambient user home or its ancestor.");
    }

    internal static bool IsLoaderInjection(string key, bool overridingRuntime = false) => key.StartsWith("LD_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
        || (overridingRuntime
            ? key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("CORECLR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CORECLR_PROFILER", StringComparison.OrdinalIgnoreCase)
            : LoaderVariables.Contains(key));
}
