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
        var deniedRelative = Path.GetRelativePath(working, denied);
        var renameAncestor = protectedParent ? """(ren "config" "moved-config") >nul 2>&1 && exit /b 96""" : "";
        // Named TYPE performs a directory search before reading; a single-file grant does not grant parent enumeration.
        // Read every fixture line through one native redirected handle and require EOF after the final line.
        // Send shell syntax through stdin instead of encoding it as a CRT command-line argument.
        var commands = $$"""
            echo stage=read 1>&2
            set "firstRead="
            set "secondRead="
            set "lastRead="
            set "extraRead="
            (
            set /p firstRead=
            set /p secondRead=
            set /p lastRead=
            set /p extraRead=
            ) <"..\readable.txt"
            if not errorlevel 1 exit /b 90
            if not "%firstRead%"=="readable" exit /b 90
            if not "%secondRead%"=="second-readable-line" exit /b 90
            if not "%lastRead%"=="last-readable-line" exit /b 90
            if defined extraRead exit /b 90
            echo read-complete:%firstRead%/%secondRead%/%lastRead%
            (dir /b ".." >nul 2>&1) && exit /b 91
            set "secretRead="
            (set /p secretRead=<"..\not-granted.txt") 2>nul
            if defined secretRead exit /b 99
            (echo forbidden>>"..\readable.txt") 2>nul && exit /b 93
            echo stage=write 1>&2
            (echo forbidden>>"{{deniedRelative}}") 2>nul && exit /b 92
            (ren "{{deniedRelative}}" "moved.yaml") >nul 2>&1 && exit /b 94
            del /f /q "{{deniedRelative}}" >nul 2>&1
            if not exist "{{deniedRelative}}" exit /b 95
            {{renameAncestor}}
            echo allowed>allowed.txt
            echo disposable>rename-before.txt
            ren rename-before.txt rename-after.txt || exit /b 97
            del /q rename-after.txt
            if exist rename-after.txt exit /b 98
            echo diagnostic 1>&2
            exit /b 0
            """;
        await using (var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [denied],
            Arguments = ["/D", "/Q"],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{commands.ReplaceLineEndings("\r\n")}\r\n"), TestContext.Current.CancellationToken);
            await consumer.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            var code = await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            var diagnostic = System.Text.RegularExpressions.Regex.Replace(stderr, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
            var failure = $"The real Windows path consumer exited with {code} (0x{unchecked((uint)code):x8}); stdoutLength={stdout.Length}; stderr={diagnostic[..Math.Min(diagnostic.Length, 2048)]}";
            Assert.True(code == 0, failure);
            Assert.True(stdout.Contains("read-complete:readable/second-readable-line/last-readable-line", StringComparison.Ordinal), failure);
            Assert.True(stderr.Contains("diagnostic", StringComparison.Ordinal), failure);
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
        AssertDaclRestored(originalDeniedAcl, denied);
        AssertInheritanceRestored(working, denied, protectedParent);
        File.WriteAllText(denied, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(denied));
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

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                WorkingDirectory = working,
                WritablePaths = [working],
                DeniedWritePaths = [settings],
                AllowNetwork = network,
                Arguments = ["/D", "/Q", "/C", "exit", "0"],
            }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(settings, TestContext.Current.CancellationToken));
        AssertDaclRestored(originalDirectoryAcl, working);
        AssertDaclRestored(originalSettingsAcl, settings);
        AssertInheritanceRestored(working, settings, protectedChild: false);
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
