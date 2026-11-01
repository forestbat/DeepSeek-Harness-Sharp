using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Dsh.ScriptHost;
using Dsh.Sandbox;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxAuthorityTests : IDisposable
{
    internal const string ProbeArgument = "--windows-sandbox-authority-probe";
    private readonly string _root = TempTree.CreateDirectory("windows-sandbox-authority");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task WritableDirectory_IsRefusedBeforePayloadAndAclChanges_AndReadonlyLeaseStillWorks()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var existing = Path.Combine(working, "existing.txt");
        File.WriteAllText(existing, "unchanged");
        SetHostAcl(working);
        SetHostAcl(existing);
        var directoryAcl = ReadDacl(working);
        var fileAcl = ReadDacl(existing);
        var marker = Path.Combine(working, "payload-started.txt");
        var rejected = CommandOptions(working) with
        {
            Arguments = ["/D", "/Q", "/C", $"echo started>\"{marker}\""], WritablePaths = [working],
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => SandboxedProcess.StartAsync(rejected, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(working, "hostile.txt")));
        Assert.False(Directory.Exists(Path.Combine(working, "hostile-directory")));
        Assert.Equal(directoryAcl, ReadDacl(working));
        Assert.Equal(fileAcl, ReadDacl(existing));
        Assert.Equal("unchanged", File.ReadAllText(existing));
        AssertHostAuthority(existing);

        await using (var control = await SandboxedProcess.StartAsync(CommandOptions(working) with
        {
            Arguments = ["/D", "/Q", "/C", "echo readonly-control"], ReadOnlyPaths = [working]
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(control.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await control.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
            Assert.Equal("readonly-control", (await output).Trim());
        }
        Assert.Equal(directoryAcl, ReadDacl(working));
        Assert.Equal(fileAcl, ReadDacl(existing));
        AssertHostAuthority(existing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task ReadonlyOrPinnedFileConsumer_BlocksHostileCreationAndRename_AndRestoresOriginalAcl(bool writableFile)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows and a host-owned private runtime deployment with ACL lease authority.");
        var relativeRuntime = Path.GetRelativePath(AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory());
        Assert.True(relativeRuntime == "." || (!Path.IsPathRooted(relativeRuntime) && relativeRuntime != ".."
            && !relativeRuntime.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "Run this consumer from a private self-contained deployment; the test must not lease an installed system runtime's ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var existing = Path.Combine(working, "existing.txt");
        File.WriteAllText(existing, "unchanged");
        SetHostAcl(working);
        SetHostAcl(existing);
        var directoryAcl = ReadDacl(working);
        var fileAcl = ReadDacl(existing);
        var options = ProbeOptions(working, existing, writableFile ? "write-file" : "readonly") with
        {
            ReadOnlyPaths = writableFile ? [] : [working],
            WritablePaths = writableFile ? [existing] : [],
        };
        // A runtime whose DACL the ordinary-user host cannot lease is an explicit unsupported prerequisite, not a passing control.
        await using (var consumer = await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            var code = await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.True(code == 0, $"The real Windows authority consumer exited with {code}: {await error}");
            Assert.Equal("hostile-creation-blocked", (await output).Trim());
        }
        Assert.False(File.Exists(Path.Combine(working, "hostile.txt")));
        Assert.False(Directory.Exists(Path.Combine(working, "hostile-directory")));
        Assert.False(File.Exists(Path.Combine(working, "renamed.txt")));
        Assert.Equal(writableFile ? "payload-write" : "unchanged", File.ReadAllText(existing));
        Assert.Equal(directoryAcl, ReadDacl(working));
        Assert.Equal(fileAcl, ReadDacl(existing));
        AssertHostAuthority(existing);
        File.WriteAllText(existing, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(existing));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task RuntimeWithoutHostSecurityAuthority_IsRefusedBeforePayload_AndRollsBackEarlierAcl()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires native Windows owner-rights access checks.");
        var witness = Path.Combine(_root, "runtime-witness.txt");
        var inaccessible = Path.Combine(_root, "runtime-no-security-authority.txt");
        File.WriteAllText(witness, "witness");
        File.WriteAllText(inaccessible, "unchanged");
        SetHostAcl(witness);
        SetHostAcl(inaccessible);
        var original = ReadDacl(witness);
        using var recovery = OpenAuthority(inaccessible);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        var hostile = $"D:P(D;;RCWDWO;;;OW)(A;;FA;;;{user.Value})";
        ApplyDacl(inaccessible, hostile);
        try
        {
            using (var denied = Native.CreateFile(inaccessible, 0xe0000, 7, 0, 3, 0x02200000, 0))
            {
                Assert.True(denied.IsInvalid);
                Assert.Equal(5, Marshal.GetLastPInvokeError());
            }
            var marker = Path.Combine(_root, "unsupported-runtime-started.txt");
            var options = CommandOptions(_root) with
            {
                RuntimePaths = [witness, inaccessible],
                Arguments = ["/D", "/Q", "/C", $"echo started>\"{marker}\""],
            };
            var failure = await Assert.ThrowsAsync<Win32Exception>(() => SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken));
            Assert.Equal(5, failure.NativeErrorCode);
            Assert.False(File.Exists(marker));
            Assert.Equal(original, ReadDacl(witness));
            Assert.Equal("witness", File.ReadAllText(witness));
            AssertHostAuthority(witness);
        }
        finally
        {
            RestoreDacl(recovery, $"D:P(A;;FA;;;{user.Value})(A;;RC;;;OW)");
        }
        AssertHostAuthority(inaccessible);
        Assert.Equal("unchanged", File.ReadAllText(inaccessible));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task PinnedFileLease_RestoresRenamedIdentity_WithoutChangingTheReplacement()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows handle-bound ACL restoration.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var original = Path.Combine(working, "original.txt");
        var renamed = Path.Combine(working, "renamed-by-host.txt");
        File.WriteAllText(original, "original");
        SetHostAcl(working);
        SetHostAcl(original);
        var originalAcl = ReadDacl(original);
        var identity = NativeFileIdentity.Get(original);
        string replacementAcl;
        await using (var consumer = await SandboxedProcess.StartAsync(CommandOptions(working) with
        {
            Arguments = ["/D", "/Q", "/C", "echo ready & set /p value= & exit /b 0"], WritablePaths = [original],
        }, TestContext.Current.CancellationToken))
        {
            using var output = new StreamReader(consumer.StandardOutput);
            var ready = await output.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.Equal("ready", ready?.Trim());
            File.Move(original, renamed);
            File.WriteAllText(original, "host replacement");
            SetHostAcl(original);
            replacementAcl = ReadDacl(original);
            var replacement = NativeFileIdentity.Get(original);
            Assert.NotEqual((identity.Device, identity.Inode), (replacement.Device, replacement.Inode));
            await consumer.StandardInput.WriteAsync("release\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await consumer.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        }
        var restored = NativeFileIdentity.Get(renamed);
        Assert.Equal((identity.Device, identity.Inode), (restored.Device, restored.Inode));
        Assert.Equal(originalAcl, ReadDacl(renamed));
        Assert.Equal(replacementAcl, ReadDacl(original));
        Assert.Equal("original", File.ReadAllText(renamed));
        Assert.Equal("host replacement", File.ReadAllText(original));
        AssertHostAuthority(renamed);
        AssertHostAuthority(original);
    }

    internal static int RunProbe(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
            return 125;
        var index = Array.IndexOf(arguments, ProbeArgument);
        if (index < 0 || arguments.Length != index + 4)
            return 124;
        var mode = arguments[index + 1];
        var working = arguments[index + 2];
        var existing = arguments[index + 3];
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("The consumer has no Windows user SID.");
            var appContainer = AppContainerSid(identity);
            var hostile = $"D:P(D;;RCWDWO;;;OW)(A;;FA;;;{user.Value})(A;;FA;;;{appContainer})";
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(hostile, 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor };
                var pointer = Marshal.AllocHGlobal(attributes.Length);
                try
                {
                    Marshal.StructureToPtr(attributes, pointer, false);
                    using var created = Native.CreateFile(Path.Combine(working, "hostile.txt"), 0x10000, 7, pointer, 1, 0x04000080, 0);
                    if (!created.IsInvalid || Marshal.GetLastPInvokeError() != 5)
                        throw new IOException("Protected-DACL file creation was not denied by the parent authority boundary.");
                }
                finally { Marshal.FreeHGlobal(pointer); }
                if (Native.CreateDirectory(Path.Combine(working, "hostile-directory"), ref attributes) || Marshal.GetLastPInvokeError() != 5)
                    throw new IOException("Protected-DACL directory creation was not denied by the parent authority boundary.");
                if (Native.SetFileSecurity(existing, 0x80000004, descriptor) || Marshal.GetLastPInvokeError() != 5)
                    throw new IOException("The consumer changed the pinned existing file's security descriptor.");
            }
            finally { Native.LocalFree(descriptor); }
            Denied(() => File.Move(existing, Path.Combine(working, "renamed.txt")));
            if (mode == "write-file")
                File.WriteAllText(existing, "payload-write");
            else
                Denied(() => File.WriteAllText(existing, "forbidden"));
            Console.WriteLine("hostile-creation-blocked");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name);
            return 123;
        }
    }

    private static void Denied(Action operation)
    {
        try { operation(); }
        catch (UnauthorizedAccessException) { return; }
        throw new IOException("A forbidden native filesystem mutation succeeded.");
    }

    [SupportedOSPlatform("windows")]
    private static string AppContainerSid(WindowsIdentity identity)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!Native.GetTokenInformation(identity.AccessToken, 31, buffer, 256, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0)
                throw new InvalidOperationException("The authority consumer is not running in an AppContainer.");
            return new SecurityIdentifier(sid).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SandboxProcessOptions ProbeOptions(string working, string existing, string mode)
    {
        var command = PtcScriptHostClient.ResolveHostCommand()!.Value;
        var runtime = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory() };
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtimeDirectory.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } dotnet } })
        {
            var host = Path.Combine(dotnet.FullName, "host");
            if (Directory.Exists(host))
                runtime.Add(host);
        }
        return new SandboxProcessOptions
        {
            Executable = command.FileName,
            Arguments = [.. command.Arguments.Where(argument => argument != PtcScriptHost.HostArgument), ProbeArgument, mode, working, existing],
            WorkingDirectory = working,
            RuntimePaths = runtime.ToArray(),
            MemoryLimitBytes = 256 * 1024 * 1024,
        };
    }

    private static SandboxProcessOptions CommandOptions(string working) => new()
    {
        Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        WorkingDirectory = working,
        MemoryLimitBytes = 128 * 1024 * 1024,
    };

    [SupportedOSPlatform("windows")]
    private static string ReadDacl(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [SupportedOSPlatform("windows")]
    private static void SetHostAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        ApplyDacl(path, $"D:P(A;;FA;;;{user.Value})(A;;RC;;;OW)");
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDacl(string path, string sddl)
    {
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!Native.SetFileSecurity(path, 0x80000004, descriptor))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { Native.LocalFree(descriptor); }
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreDacl(SafeFileHandle handle, string sddl)
    {
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!Native.GetSecurityDescriptorDacl(descriptor, out _, out var dacl, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var error = Native.SetSecurityInfo(handle, 1, 0x80000004, 0, 0, dacl, 0);
            if (error != 0)
                throw new Win32Exception((int)error);
        }
        finally { Native.LocalFree(descriptor); }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenAuthority(string path)
    {
        var handle = Native.CreateFile(path, 0xe0000, 7, 0, 3, 0x02200000, 0);
        if (!handle.IsInvalid)
            return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error, "The host cannot open the test object with its original security authority.");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertHostAuthority(string path)
    {
        using var handle = OpenAuthority(path);
        Assert.False(handle.IsInvalid);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
        [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetFileSecurity(string path, uint information, nint descriptor);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out nint converted, out uint size);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSecurityDescriptorDacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, nint owner, nint group, nint dacl, nint sacl);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, nint buffer, uint size, out uint required);
        [DllImport("kernel32.dll")]
        internal static extern nint LocalFree(nint pointer);
    }
}
