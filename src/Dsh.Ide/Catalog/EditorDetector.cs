using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Dsh.Core;
using Microsoft.Win32;
using Dsh.Boot;

namespace Dsh.Ide.Catalog;

public sealed class EditorDetector(
    IReadOnlyDictionary<string, string[]>? installationRoots = null,
    IReadOnlyList<string>? bundleRoots = null,
    IReadOnlyList<string>? desktopRoots = null)
{
    private const int DiscoveryTimeoutMilliseconds = 3000;
    private readonly IReadOnlyDictionary<string, string[]> installationRoots = installationRoots
        ?? EditorDiscoveryPaths.Read(PluginSettingsSection.DefaultParameter(Plugin.Ide, "installation_roots"));
    private readonly IReadOnlyList<string> bundleRoots = bundleRoots ?? ConfigMap.Strings(PluginSettingsSection.DefaultParameter(Plugin.Ide, "bundle_roots"));
    private readonly IReadOnlyList<string> desktopRoots = desktopRoots ?? ConfigMap.Strings(PluginSettingsSection.DefaultParameter(Plugin.Ide, "desktop_roots"));
    public EditorInstallation? Detect(EditorDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        foreach (var (path, source, bundle) in Candidates(descriptor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bundle ? Directory.Exists(path) : IsExecutable(path))
                return new(descriptor.Id, Path.GetFullPath(path), source, ReadVersion(path, bundle), bundle);
        }
        return null;
    }
    public static string? FindOnPath(string command)
    {
        if (Path.IsPathRooted(command)) return IsExecutable(command) ? command : null;
        var suffixes = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? new[] { "", ".exe", ".cmd", ".bat", ".com" } : new[] { "" };
        foreach (var root in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(root.Trim('"'), $"{command}{suffix}");
                if (IsExecutable(candidate)) return Path.GetFullPath(candidate);
            }
        return null;
    }
    private IEnumerable<(string Path, string Source, bool Bundle)> Candidates(EditorDescriptor d)
    {
        foreach (var command in d.RemoteExecutableCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindOnPath(command) is { } found) yield return (found, "remote-executable", false);
        if (Path.IsPathRooted(d.Command)) { yield return (d.Command, "configuration", false); yield break; }
        var names = new[] { d.Command }.Concat(d.ExecutableCandidates).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var command in names)
            if (FindOnPath(command) is { } found) yield return (found, "PATH", false);
        foreach (var template in installationRoots.GetValueOrDefault(EditorDiscoveryPaths.Platform) ?? [])
            foreach (var root in EditorDiscoveryPaths.Directories(EditorDiscoveryPaths.Render(template, d)))
                foreach (var name in names) yield return (Path.Combine(root, name), "configured-location", false);
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in RegistryExecutables(names)) yield return (path, "registry", false);
        }
        foreach (var discovery in d.DiscoveryCommands)
            if (discovery.Platform.Equals(EditorDiscoveryPaths.Platform, StringComparison.Ordinal)
                && DiscoverExecutable(discovery, d) is { } executable)
                yield return (executable, "configured-discovery", false);
        if (OperatingSystem.IsMacOS())
            foreach (var template in bundleRoots)
                foreach (var name in d.BundleNames)
                {
                    var root = EditorDiscoveryPaths.Render(template, d);
                    var bundle = Path.Combine(root, $"{name}.app");
                    var cli = Path.Combine(bundle, "Contents", "Resources", "app", "bin", d.Command);
                    yield return (cli, "application-bundle-cli", false);
                    yield return (Path.Combine(bundle, "Contents", "MacOS", d.Command), "application-bundle-cli", false);
                    yield return (bundle, "application-bundle", true);
                }
        if (OperatingSystem.IsLinux())
            foreach (var template in desktopRoots)
                foreach (var name in names.Append(d.Id.Value).Select(Path.GetFileNameWithoutExtension).Distinct())
                {
                    var root = EditorDiscoveryPaths.Render(template, d);
                    var entry = Path.Combine(root, $"{name}.desktop");
                    if (ReadDesktopExecutable(entry) is { } executable) yield return (executable, "desktop-entry", false);
                }
    }
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryExecutables(string[] names)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var name in names)
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
                if (key?.GetValue(null) is string value) yield return value.Trim('"');
            }
    }
    private static string? DiscoverExecutable(EditorDiscoveryCommand discovery, EditorDescriptor editor)
    {
        var executable = FindOnPath(EditorDiscoveryPaths.Render(discovery.Command, editor));
        if (executable is null) return null;
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } };
        foreach (var argument in discovery.Arguments) process.StartInfo.ArgumentList.Add(EditorDiscoveryPaths.Render(argument, editor));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(DiscoveryTimeoutMilliseconds)) { process.Kill(true); process.WaitForExit(); return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    private static string? ReadDesktopExecutable(string entry)
    {
        if (!File.Exists(entry)) return null;
        var line = File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        if (line is null) return null;
        var value = line[5..].Trim();
        var command = value.StartsWith('"') ? value[1..].Split('"')[0] : value.Split(' ', 2)[0];
        return FindOnPath(command);
    }
    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    private static string? ReadVersion(string path, bool bundle)
    {
        if (bundle)
        {
            var plist = Path.Combine(path, "Contents", "Info.plist");
            if (!File.Exists(plist)) return null;
            try
            {
                var document = XDocument.Load(plist);
                return document.Descendants("key").FirstOrDefault(k => k.Value == "CFBundleShortVersionString")?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
            }
            catch (System.Xml.XmlException) { return null; }
        }
        return OperatingSystem.IsWindows() ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
    }
}
