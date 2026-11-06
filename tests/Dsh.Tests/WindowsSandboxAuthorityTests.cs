using Dsh.Boot;
using Dsh.Plugins.Sandbox;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Dsh.Sandbox;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxAuthorityTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-sandbox-authority");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task PathGrants_EnforceReadsAndWrites_AndAreRemovedAfterDisposal()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var denied = Path.Combine(working, "settings.yaml");
        const string readable = "readable\r\nsecond-readable-line\r\nlast-readable-line\r\n";
        File.WriteAllText(input, readable);
        File.WriteAllText(secret, "host-only");
        var settingsBytes = "unchanged"u8.ToArray();
        File.WriteAllBytes(denied, settingsBytes);
        var originalParentAcl = ReadDacl(_root);
        var originalSecretAcl = ReadDacl(secret);
        var originalDirectoryAcl = ReadDacl(working);
        var originalInputAcl = ReadDacl(input);
        var originalDeniedAcl = ReadDacl(denied);
        var launch = PluginSandboxLaunch.Options(SandboxRuntimePaths.ForCurrentHost());
        await using (var consumer = await SandboxedProcess.StartAsync(launch with
        {
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [denied],
            Arguments = [.. launch.Arguments.Take(launch.Arguments.Count - 1), SandboxFileGrantConsumer.Argument, input, secret, denied, bool.FalseString],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.WriteAsync("native-pipe\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await consumer.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            var code = await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            var diagnostic = System.Text.RegularExpressions.Regex.Replace(stderr, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
            var consequences = "";
            if (code != 0)
            {
                var settingsUnchanged = File.Exists(denied) && (await File.ReadAllBytesAsync(denied, TestContext.Current.CancellationToken)).AsSpan().SequenceEqual(settingsBytes);
                var inputUnchanged = File.Exists(input) && await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken) == readable;
                consequences = $"allowedExists={File.Exists(Path.Combine(working, "allowed.txt"))}; renameBeforeExists={File.Exists(Path.Combine(working, "rename-before.txt"))}; renameAfterExists={File.Exists(Path.Combine(working, "rename-after.txt"))}; settingsUnchanged={settingsUnchanged}; inputUnchanged={inputUnchanged}";
            }
            var failure = $"The real Windows path consumer exited with {code} (0x{unchecked((uint)code):x8}); {consequences}; stdoutLength={stdout.Length}; stderr={diagnostic[..Math.Min(diagnostic.Length, 2048)]}";
            Assert.True(code == 0, failure);
            Assert.True(stdout.Contains("read-complete:readable/second-readable-line/last-readable-line", StringComparison.Ordinal), failure);
            Assert.Contains(SandboxFileGrantConsumer.Input, stdout);
            Assert.True(stderr.Contains("diagnostic", StringComparison.Ordinal), failure);
            Assert.True(!File.Exists(Path.Combine(working, "rename-before.txt")), failure);
            Assert.True(!File.Exists(Path.Combine(working, "rename-after.txt")), failure);
        }
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")).Trim());
        Assert.Equal(settingsBytes, File.ReadAllBytes(denied));
        Assert.Equal(readable, File.ReadAllText(input));
        Assert.Equal("host-only", File.ReadAllText(secret));
        AssertDaclRestored(originalParentAcl, _root);
        AssertDaclRestored(originalSecretAcl, secret);
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalInputAcl, input);
        AssertDaclRestored(originalDeniedAcl, denied);
        File.WriteAllText(denied, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(denied));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task UnreadableRoot_RefusesLaunchAndRollsBackEarlierGrants()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var readable = Directory.CreateDirectory(Path.Combine(_root, "readonly"));
        var originalDirectoryAcl = ReadDacl(working);
        var originalReadableAcl = ReadDacl(readable.FullName);
        var inaccessible = readable.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        inaccessible.AddAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.ReadPermissions, AccessControlType.Deny));
        // An OWNER RIGHTS ACE suppresses implicit owner READ_CONTROL while retaining WRITE_DAC for restoration.
        inaccessible.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.ChangePermissions, AccessControlType.Allow));
        readable.SetAccessControl(inaccessible);
        var resumedMarker = Path.Combine(working, "resumed.txt");
        try
        {
            var failure = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(async () =>
            {
                await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
                {
                    Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    WorkingDirectory = working,
                    ReadOnlyPaths = [readable.FullName],
                    WritablePaths = [working],
                    Arguments = ["/D", "/Q", "/C", "echo resumed>resumed.txt"],
                }, TestContext.Current.CancellationToken);
            });
            Assert.Equal(5, failure.NativeErrorCode);
            AssertDaclRestored(originalDirectoryAcl, working);
            Assert.False(File.Exists(resumedMarker));
        }
        finally
        {
            inaccessible.SetSecurityDescriptorSddlForm(originalReadableAcl, AccessControlSections.Access);
            readable.SetAccessControl(inaccessible);
        }
    }

    private static void AssertDaclRestored(string expected, string path)
    {
        var before = new RawSecurityDescriptor(expected);
        var after = new RawSecurityDescriptor(ReadDacl(path));
        // SetSecurityInfo converts legacy ACLs to automatic inheritance without changing their ACE semantics.
        // Permit only that one-way metadata transition; retain every ACE, its order and flags, and all other control bits.
        // https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces
        if ((before.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) == 0)
            after.SetFlags(after.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        Assert.Equal(before.ControlFlags, after.ControlFlags);
        Assert.Equal(before.GetSddlForm(AccessControlSections.Access), after.GetSddlForm(AccessControlSections.Access));
    }

    private static string ReadDacl(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
}
