using Dsh.Boot;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task PathGrants_EnforceReadsAndWrites_AndAreRemovedAfterDisposal(bool protectedParent)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var protectedDirectoryInfo = protectedParent
            ? Directory.CreateDirectory(Path.Combine(working, "config")) : new DirectoryInfo(working);
        var protectedDirectory = protectedDirectoryInfo.FullName;
        if (protectedParent)
        {
            var protectedAcl = protectedDirectoryInfo.GetAccessControl(AccessControlSections.Access);
            protectedAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            protectedDirectoryInfo.SetAccessControl(protectedAcl);
        }
        var denied = Path.Combine(protectedDirectory, "settings.yaml");
        const string readable = "readable\r\nsecond-readable-line\r\nlast-readable-line\r\n";
        File.WriteAllText(input, readable);
        File.WriteAllText(secret, "host-only");
        var settingsBytes = "unchanged"u8.ToArray();
        File.WriteAllBytes(denied, settingsBytes);
        var originalParentAcl = ReadDacl(_root);
        var originalProtectedDirectoryAcl = ReadDacl(protectedDirectory);
        var originalSecretAcl = ReadDacl(secret);
        var originalDirectoryAcl = ReadDacl(working);
        var originalInputAcl = ReadDacl(input);
        var originalDeniedAcl = ReadDacl(denied);
        string expectedDeniedAcl;
        var launch = PluginSandboxLaunch.Options(SandboxRuntimePaths.ForCurrentHost());
        await using (var consumer = await SandboxedProcess.StartAsync(launch with
        {
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [denied],
            Arguments = [.. launch.Arguments.Take(launch.Arguments.Count - 1), SandboxFileGrantConsumer.Argument, input, secret, denied, protectedParent.ToString()],
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
            expectedDeniedAcl = AddConcurrentReadAttributeGrant(denied, originalDeniedAcl);
        }
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")).Trim());
        Assert.Equal(settingsBytes, File.ReadAllBytes(denied));
        Assert.Equal(readable, File.ReadAllText(input));
        Assert.Equal("host-only", File.ReadAllText(secret));
        AssertDaclRestored(originalParentAcl, _root);
        AssertDaclRestored(originalProtectedDirectoryAcl, protectedDirectory);
        AssertDaclRestored(originalSecretAcl, secret);
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalInputAcl, input);
        AssertDaclRestored(expectedDeniedAcl, denied);
        AssertInheritanceRestored(working, denied, protectedParent);
        File.WriteAllText(denied, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(denied));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task OverlappingPathGrants_DisposingOneOwnerPreservesTheOtherConsumer()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var settings = Path.Combine(working, "settings.yaml");
        File.WriteAllText(input, SandboxFileGrantConsumer.Readable);
        File.WriteAllText(secret, "host-only");
        File.WriteAllText(settings, "unchanged");
        var originalDirectoryAcl = ReadDacl(working);
        var originalInputAcl = ReadDacl(input);
        var originalSettingsAcl = ReadDacl(settings);
        var launch = PluginSandboxLaunch.Options(SandboxRuntimePaths.ForCurrentHost());
        var options = launch with
        {
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [settings],
            Arguments = [.. launch.Arguments.Take(launch.Arguments.Count - 1), SandboxFileGrantConsumer.Argument, input, secret, settings, bool.FalseString],
        };
        await using (var first = await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken))
        await using (var second = await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken))
        {
            // Both native processes own the same paths, but neither has received its input.
            await first.DisposeAsync();
            var output = new StreamReader(second.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(second.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await second.StandardInput.WriteAsync("native-pipe\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await second.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await second.StandardInput.DisposeAsync();
            Assert.Equal(0, await second.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Deadline, TestContext.Current.CancellationToken));
            Assert.Contains(SandboxFileGrantConsumer.Input, await output);
            Assert.Contains("diagnostic", await error);
        }
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")));
        Assert.Equal("unchanged", File.ReadAllText(settings));
        Assert.Equal("host-only", File.ReadAllText(secret));
        Assert.Equal(SandboxFileGrantConsumer.Readable, File.ReadAllText(input));
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalInputAcl, input);
        AssertDaclRestored(originalSettingsAcl, settings);
        AssertInheritanceRestored(working, settings, protectedChild: false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "SandboxNative")]
    public async Task ExistingReadAuthority_PreservesReadonlyAccessForRuntimeCreatedDescendants(bool inheritable, bool protectedRoot)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem access checks.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var readable = Directory.CreateDirectory(Path.Combine(_root, "readonly"));
        var acl = readable.GetAccessControl(AccessControlSections.Access);
        if (protectedRoot)
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"), FileSystemRights.ReadAndExecute,
            inheritable ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        readable.SetAccessControl(acl);
        var input = Path.Combine(readable.FullName, "input.txt");
        File.WriteAllText(input, "initial-read");
        var originalAcl = ReadDacl(readable.FullName);
        string lateFile;
        await using (var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = working,
            ReadOnlyPaths = [readable.FullName],
            Arguments = ["/D", "/Q", "/C",
                @"set /p gate=&type ..\readonly\input.txt&type ..\readonly\new\late.txt&(echo tampered>>..\readonly\input.txt) 2>nul&exit /b 0"],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = consumer.StandardError.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
            // Runtime-created descendants exercise inherited authority after the real consumer starts and before its input is released.
            var lateDirectory = Directory.CreateDirectory(Path.Combine(readable.FullName, "new")).FullName;
            lateFile = Path.Combine(lateDirectory, "late.txt");
            File.WriteAllText(lateFile, "late-read");
            await consumer.StandardInput.WriteAsync("continue\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            Assert.Equal(0, await consumer.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Deadline, TestContext.Current.CancellationToken));
            var stdout = await output;
            await error;
            Assert.Contains("initial-read", stdout);
            Assert.Contains("late-read", stdout);
            Assert.Equal("initial-read", File.ReadAllText(input));
        }
        AssertDaclRestored(originalAcl, readable.FullName);
        var hostAfter = Path.Combine(Path.GetDirectoryName(lateFile)!, "host-after.txt");
        File.WriteAllText(hostAfter, "host-after");
        Assert.Equal(ReadDacl(hostAfter), ReadDacl(lateFile));
    }

    [Theory]
    [InlineData("S-1-15-2-1", false)]
    [InlineData("S-1-15-2-2", false)]
    [InlineData("S-1-15-3-1", true)]
    [InlineData("S-1-15-3-2", true)]
    [InlineData("S-1-15-3-3", true)]
    [Trait("Category", "SandboxNative")]
    public async Task ProtectedPath_WithAmbientAppContainerWriteGrant_RefusesLaunchWithoutChangingOtherPrincipals(string ambientSid, bool network)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var settings = Path.Combine(working, "settings.yaml");
        var settingsBytes = "unchanged"u8.ToArray();
        await File.WriteAllBytesAsync(settings, settingsBytes, TestContext.Current.CancellationToken);
        var file = new FileInfo(settings);
        var acl = file.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(ambientSid), FileSystemRights.WriteData, AccessControlType.Allow));
        file.SetAccessControl(acl);
        var originalDirectoryAcl = ReadDacl(working);
        var originalSettingsAcl = ReadDacl(settings);
        var resumedMarker = Path.Combine(working, "resumed.txt");

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = working,
                WritablePaths = [working],
                DeniedWritePaths = [settings],
                AllowNetwork = network,
                Arguments = ["/D", "/Q", "/C", "echo resumed>resumed.txt"],
            }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(settings, TestContext.Current.CancellationToken));
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalSettingsAcl, settings);
        AssertInheritanceRestored(working, settings, protectedChild: false);
        Assert.False(File.Exists(resumedMarker));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task ReadOnlyDirectory_WithAmbientInheritOnlyWriteGrant_RefusesLaunchAndPreservesAcl()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var directory = Directory.CreateDirectory(Path.Combine(_root, "readonly"));
        var acl = directory.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        var originalAcl = ReadDacl(directory.FullName);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = directory.FullName,
                ReadOnlyPaths = [directory.FullName],
                Arguments = ["/D", "/Q", "/C", "exit", "0"],
            }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(originalAcl, ReadDacl(directory.FullName));
    }

    private static string AddConcurrentReadAttributeGrant(string path, string original)
    {
        // This synthetic SID is a test-only principal, not a real account.
        var principal = new SecurityIdentifier("S-1-5-21-0-0-0-271828");
        var rule = new FileSystemAccessRule(principal, FileSystemRights.ReadAttributes, AccessControlType.Allow);
        var expected = new FileSecurity();
        expected.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
        expected.AddAccessRule(rule);
        var file = new FileInfo(path);
        var current = file.GetAccessControl(AccessControlSections.Access);
        current.AddAccessRule(rule);
        file.SetAccessControl(current);
        return expected.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
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

    private static void AssertInheritanceRestored(string parent, string child, bool protectedChild)
    {
        var marker = new SecurityIdentifier("S-1-5-21-0-0-0-314159");
        var rule = new FileSystemAccessRule(marker, FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        var directory = new DirectoryInfo(parent);
        var originalParent = ReadDacl(parent);
        var originalChild = ReadDacl(child);
        var acl = directory.GetAccessControl(AccessControlSections.Access);
        var created = Path.Combine(parent, "host-created-after-disposal.txt");
        acl.AddAccessRule(rule);
        directory.SetAccessControl(acl);
        try
        {
            var inherited = new FileInfo(child).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(access => marker.Equals(access.IdentityReference)
                    && access.IsInherited && access.FileSystemRights == rule.FileSystemRights);
            Assert.Equal(!protectedChild, inherited);
            File.WriteAllText(created, "host-created");
            Assert.Contains(new FileInfo(created).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(), access => marker.Equals(access.IdentityReference)
                    && access.IsInherited && access.FileSystemRights == rule.FileSystemRights);
        }
        finally
        {
            acl.RemoveAccessRuleSpecific(rule);
            directory.SetAccessControl(acl);
            File.Delete(created);
        }
        AssertDaclRestored(originalParent, parent);
        AssertDaclRestored(originalChild, child);
    }

    private static string ReadDacl(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
}
