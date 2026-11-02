using System.Collections;
using System.Runtime.InteropServices;
using Dsh.Sandbox;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class SandboxPinnedInputTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("sandbox-pinned-inputs");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxSymlinkedExecutable_ReadOnlyParentPreservesTheLinkAndPinnedTarget()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Linux descriptor mounts preserve links already supplied by a read-only directory.");
        RequireUnixPerl();
        var code = Directory.CreateDirectory(Path.Combine(_root, "code")).FullName;
        var alias = Path.Combine(code, "runner");
        File.CreateSymbolicLink(alias, "/usr/bin/perl");
        const string script = """
            my $alias = $ARGV[0];
            die "the granted executable link was not preserved\n" unless -l $alias && -r $alias;
            die "the read-only executable link was removable\n" if unlink($alias);
            print "protected-link\n";
            """;
        await AssertSuccessfulAsync(Options(code) with
        {
            Executable = alias,
            Arguments = ["-f", "-e", script, "--", alias],
            RuntimePaths = RuntimePaths().Append(code).ToArray()
        }, "protected-link\n");
        Assert.True(File.Exists(alias));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("runtime")]
    [InlineData("denied")]
    [Trait("Category", "SandboxNative")]
    public async Task UnixProtectedAncestor_CannotBeRenamedAndRecreatedButSiblingWritesRemainAllowed(string grant)
    {
        RequireUnixPerl();
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var ancestor = Directory.CreateDirectory(Path.Combine(working, "ancestor")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(ancestor, "nested")).FullName;
        var target = Path.Combine(nested, "protected.txt");
        File.WriteAllText(target, "unchanged");
        const string script = """
            my ($root, $ancestor, $nested, $target) = @ARGV;
            for my $directory ($ancestor, $nested) {
                my $renamed = rename($directory, "$directory-moved");
                my $recreated = mkdir($directory);
                die "protected namespace was replaceable\n" if $renamed || $recreated;
            }
            if (open(my $bad, '>', $target)) { die "protected file was writable\n"; }
            for my $directory ($root, $ancestor, $nested) {
                open(my $good, '>', "$directory/sibling.txt") or die "sibling write was denied\n";
                print {$good} 'allowed';
                close($good) or die "sibling close failed\n";
            }
            print "protected\n";
            """;
        var options = Options(working) with
        {
            Arguments = ["-f", "-e", script, "--", working, ancestor, nested, target],
            WritablePaths = [working],
            ReadOnlyPaths = grant == "read" ? [target] : [],
            RuntimePaths = grant == "runtime" ? RuntimePaths().Append(target).ToArray() : RuntimePaths(),
            DeniedWritePaths = grant == "denied" ? [target] : []
        };
        await AssertSuccessfulAsync(options, "protected\n");
        Assert.Equal("unchanged", File.ReadAllText(target));
        foreach (var directory in new[] { working, ancestor, nested })
        {
            Assert.Equal("allowed", File.ReadAllText(Path.Combine(directory, "sibling.txt")));
            Assert.False(Directory.Exists(directory + "-moved"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "SandboxNative")]
    public async Task UnixReadOnlyInputs_ProtectExistingHardlinksWithoutRejectingUnrelatedAliases(bool runtimeInput, bool directoryInput)
    {
        RequireUnixPerl();
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var inputs = Directory.CreateDirectory(Path.Combine(_root, "inputs")).FullName;
        var target = Path.Combine(inputs, "protected.txt");
        var alias = Path.Combine(working, "protected\\alias.txt");
        var unrelated = Path.Combine(working, "unrelated.txt");
        var unrelatedAlias = Path.Combine(working, "unrelated-alias.txt");
        File.WriteAllText(target, "read-only");
        File.WriteAllText(unrelated, "before");
        Assert.Equal(0, link(target, alias));
        Assert.Equal(0, link(unrelated, unrelatedAlias));
        Assert.Equal(NativeFileIdentity.Get(target).Inode, NativeFileIdentity.Get(alias).Inode);
        var input = directoryInput ? inputs : target;
        const string script = """
            my ($protected, $unrelated, $allowed, @grants) = @ARGV;
            if (open(my $bad, '>', $protected)) { die "protected alias was writable\n"; }
            open(my $good, '>', $unrelated) or die "unrelated alias was denied\n";
            print {$good} 'unrelated-write-allowed';
            close($good) or die "unrelated close failed\n";
            open(my $new, '>', $allowed) or die "ordinary write was denied\n";
            print {$new} 'allowed';
            close($new) or die "ordinary close failed\n";
            if ($^O eq 'linux') {
                opendir(my $fds, '/proc/self/fd') or die "cannot inspect payload descriptors\n";
                my @fds = readdir($fds);
                closedir($fds);
                for my $fd (@fds) {
                    next unless $fd =~ /^\d+$/ && $fd > 2;
                    my @opened = stat("/proc/self/fd/$fd");
                    next unless @opened;
                    for my $grant ($protected, @grants) {
                        my @expected = stat($grant);
                        die "grant descriptor survived into payload\n"
                            if @expected && $opened[0] == $expected[0] && $opened[1] == $expected[1];
                    }
                }
            }
            print "protected\n";
            """;
        var options = Options(working) with
        {
            Arguments = ["-f", "-e", script, "--", alias, unrelatedAlias, Path.Combine(working, "allowed.txt"), working, input, "/usr/bin/perl", .. RuntimePaths()],
            WritablePaths = [working],
            ReadOnlyPaths = runtimeInput ? [] : [input],
            RuntimePaths = runtimeInput ? RuntimePaths().Append(input).ToArray() : RuntimePaths()
        };
        await AssertSuccessfulAsync(options, "protected\n");
        Assert.Equal("read-only", File.ReadAllText(target));
        Assert.Equal("read-only", File.ReadAllText(alias));
        Assert.Equal("unrelated-write-allowed", File.ReadAllText(unrelated));
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("runtime")]
    [InlineData("write")]
    [InlineData("code")]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxCheckedAncestorReplacement_BindsTheValidatedObjectInsteadOfTheReplacement(string grant)
    {
        RequireUnixPerl();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Descriptor-bound mount source validation is Linux-specific.");
            return;
        }
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var parent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var ancestor = Directory.CreateDirectory(Path.Combine(parent, "checked")).FullName;
        var moved = Path.Combine(parent, "moved");
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement")).FullName;
        var input = Path.Combine(ancestor, "input.txt");
        File.WriteAllText(input, "checked");
        File.WriteAllText(Path.Combine(replacement, "input.txt"), "replacement");
        var executable = Path.Combine(ancestor, "tool");
        if (grant == "code")
        {
            File.Copy("/usr/bin/perl", executable);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var replacementExecutable = Path.Combine(replacement, "tool");
            File.WriteAllText(replacementExecutable, "#!/bin/sh\nprintf 'replacement\\n'\n");
            File.SetUnixFileMode(replacementExecutable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var swapped = false;
        var environment = new DuringEnvironmentEnumeration(() =>
        {
            Directory.Move(ancestor, moved);
            Directory.CreateSymbolicLink(ancestor, replacement);
            swapped = true;
        });
        var script = grant == "code"
            ? "print \"checked\\n\";"
            : grant == "write"
                ? "open(my $out, '>', $ARGV[0]) or die; print {$out} 'allowed'; close($out) or die; print \"checked\\n\";"
                : "open(my $in, '<', $ARGV[0]) or die; local $/; print <$in>, \"\\n\";";
        var options = Options(working) with
        {
            Executable = grant == "code" ? executable : "/usr/bin/perl",
            Arguments = ["-f", "-e", script, "--", grant == "write" ? Path.Combine(ancestor, "allowed.txt") : input],
            ReadOnlyPaths = grant == "read" ? [input] : [],
            RuntimePaths = grant == "runtime" ? RuntimePaths().Append(ancestor).ToArray() : RuntimePaths(),
            WritablePaths = grant == "write" ? [working, ancestor] : [working],
            Environment = environment
        };
        try
        {
            await AssertSuccessfulAsync(options, "checked\n");
            Assert.True(swapped);
            Assert.Equal("checked", File.ReadAllText(Path.Combine(moved, "input.txt")));
            Assert.Equal("replacement", File.ReadAllText(Path.Combine(replacement, "input.txt")));
            if (grant == "write")
            {
                Assert.Equal("allowed", File.ReadAllText(Path.Combine(moved, "allowed.txt")));
                Assert.False(File.Exists(Path.Combine(replacement, "allowed.txt")));
            }
        }
        finally
        {
            if (swapped)
            {
                Directory.Delete(ancestor);
                Directory.Move(moved, ancestor);
            }
        }
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task UnixOverdeepWritableTree_FailsClosedBeforeThePayloadCanWrite()
    {
        RequireUnixPerl();
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var nested = working;
        for (var depth = 0; depth < 129; depth++)
            nested = Directory.CreateDirectory(Path.Combine(nested, "d")).FullName;
        var target = Path.Combine(_root, "protected.txt");
        var alias = Path.Combine(working, "protected-alias.txt");
        File.WriteAllText(target, "unchanged");
        Assert.Equal(0, link(target, alias));
        var marker = Path.Combine(working, "payload-ran.txt");
        var options = Options(working) with
        {
            WritablePaths = [working], ReadOnlyPaths = [target],
            Arguments = ["-f", "-e", "open(my $out, '>', $ARGV[0]) or die; print {$out} 'ran';", "--", marker]
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void HandleIdentity_RemainsTheOpenedObjectAfterPathReplacement()
    {
        var path = Path.Combine(_root, "input.txt");
        var moved = Path.Combine(_root, "moved.txt");
        File.WriteAllText(path, "original");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var original = NativeFileIdentity.Get(handle);
        Assert.True(original.IsRegular);
        File.Move(path, moved);
        File.WriteAllText(path, "replacement");
        Assert.Equal(original, NativeFileIdentity.Get(handle));
        Assert.Equal(original, NativeFileIdentity.Get(moved));
        var replacement = NativeFileIdentity.Get(path);
        Assert.True(original.Device != replacement.Device || original.Inode != replacement.Inode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandleIdentity_RuleNormalizedPathsOpenTheActualObject(bool extended)
    {
        if (extended && !OperatingSystem.IsWindows())
            Assert.Skip("Extended DOS path prefixes are Windows-specific.");
        var path = Path.Combine(_root, "rule-input-é-λ.txt");
        var alias = Path.Combine(_root, "alias-é-λ.txt");
        var other = Path.Combine(_root, "different-input-é-λ.txt");
        File.WriteAllText(path, "rule-selected object");
        File.WriteAllText(other, "a different object");
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLinkWindows(alias, path, 0));
        else Assert.Equal(0, link(path, alias));
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var expected = NativeFileIdentity.Get(handle);
        Assert.True(expected.IsRegular);
        string[] spellings = OperatingSystem.IsWindows()
            ? [path, alias, PermissionEngine.PathText(path), PermissionEngine.PathText(alias),
                $"{Path.GetDirectoryName(path)}/{Path.GetFileName(path)}",
                $"{Path.GetDirectoryName(alias)}/{Path.GetFileName(alias)}"]
            : [path, alias];
        foreach (var spelling in spellings)
            Assert.Equal(expected, NativeFileIdentity.Get(extended ? $@"\\?\{spelling}" : spelling));
        var unrelated = NativeFileIdentity.Get(PermissionEngine.PathText(other));
        Assert.NotEqual((expected.Device, expected.Inode), (unrelated.Device, unrelated.Inode));
    }

    private static async Task AssertSuccessfulAsync(SandboxProcessOptions options, string expected)
    {
        await using var sandbox = await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox exited with {exit}: {await error}");
        Assert.Equal(expected, await output);
    }

    private static SandboxProcessOptions Options(string working) => new()
    {
        Executable = "/usr/bin/perl", WorkingDirectory = working, RuntimePaths = RuntimePaths(), MemoryLimitBytes = 128 * 1024 * 1024
    };

    private static string[] RuntimePaths() => new[] { "/lib", "/lib64", "/usr/lib" }.Where(Directory.Exists).ToArray();

    private static void RequireUnixPerl()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) || !File.Exists("/usr/bin/perl"))
            Assert.Skip("This native IO regression requires Linux or macOS with the system Perl executable.");
    }

    private sealed class DuringEnvironmentEnumeration(Action action) : IReadOnlyDictionary<string, string?>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => [];
        public IEnumerable<string?> Values => [];
        public string? this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string? value) { value = null; return false; }
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            action();
            return Enumerable.Empty<KeyValuePair<string, string?>>().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string alias);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string path, string existing, nint securityAttributes);
}
