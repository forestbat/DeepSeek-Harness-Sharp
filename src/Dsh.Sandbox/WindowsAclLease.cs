using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Grants a unique AppContainer SID access to declared paths and removes those grants on disposal.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAclLease : IDisposable
{
    private const int ReadExecute = 0x1200a9;
    private const int Modify = 0x1301bf;
    private const int Delete = 0x10000;
    private const int WriteData = 2;
    private const uint ProtectDacl = 0x80000000;
    private const uint UnprotectDacl = 0x20000000;
    private const ushort DaclProtected = 0x1000;
    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");
    private static readonly SecurityIdentifier AllRestrictedApplicationPackages = new("S-1-15-2-2");
    private static readonly SecurityIdentifier[] NetworkCapabilitySids =
        Array.ConvertAll(WindowsNative.NetworkCapabilities, static sid => new SecurityIdentifier(sid));
    private static readonly Lock AclGate = new();
    private static readonly Dictionary<string, WritableLabel> WritableLabels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TemporaryInheritance> ProtectedInheritance = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _entries = [];
    private readonly SecurityIdentifier _sid;
    private readonly AclMeasurements _measurements = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    private WindowsAclLease(SecurityIdentifier sid) => _sid = sid;

    public static WindowsAclLease CreateBootstrap(nint sid, SandboxPolicy policy, CancellationToken cancellationToken)
    {
        var lease = new WindowsAclLease(new SecurityIdentifier(sid));
        lease._cancellationRegistration = cancellationToken.UnsafeRegister(static state => _ = ((AclMeasurements)state!).ReportAsync(0), lease._measurements);
        try
        {
            // Bootstrap grants apply only to the executable and working-directory objects before the suspended token is available.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            GrantObject(policy.Executable, directory: false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(policy.Executable, policy.WorkingDirectory))
                GrantObject(policy.WorkingDirectory, directory: true);
            return lease;

            void GrantObject(string path, bool directory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SandboxPolicy.Contains(windows, path))
                    return;
                var entry = new Entry(path, lease._measurements, 0, bootstrap: true, bootstrapDirectory: directory);
                lease._entries.Add(entry);
                entry.Grant(lease._sid, ReadExecute, ReadExecute, false, false, policy.AllowNetwork, null, cancellationToken);
            }
        }
        catch (Exception creationFailure)
        {
            try { lease.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Creating and cleaning up the AppContainer filesystem grants failed.", creationFailure, cleanupFailure);
            }
            throw;
        }
    }

    public void Apply(SandboxPolicy policy, SafeFileHandle process, CancellationToken cancellationToken)
    {
        using var effectiveRead = EffectiveRead.TryCreate(process, _measurements);
        var paths = new Dictionary<string, (bool Write, bool Directory, int Class)>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var grant in policy.Writable.OrderBy(static grant => grant.Source.Length))
            if (!SandboxPolicy.Contains(windows, grant.Source))
                AddTree(paths, grant.Source, grant.IsDirectory, true, 1, cancellationToken);
        foreach (var grant in policy.Readable.OrderBy(static grant => grant.Source.Length))
            if (!SandboxPolicy.Contains(windows, grant.Source))
                AddTree(paths, grant.Source, grant.IsDirectory, false, 0, cancellationToken);
        foreach (var denied in policy.Denied)
        {
            if (File.Exists(denied) || Directory.Exists(denied))
                AddTree(paths, denied, Directory.Exists(denied), false, 2, cancellationToken);
            else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
        }
        var checkedAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, access) in paths)
            for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (paths.ContainsKey(parent) || !checkedAncestors.Add(parent))
                    break;
                _measurements.Set(1, access.Class);
                Entry.DemandAmbientAncestorGrant(parent, policy.AllowNetwork);
                _measurements.Set(12, access.Class);
            }
        foreach (var (path, access) in paths.OrderBy(static entry => entry.Key.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var denyWrite = !access.Write;
            var denyDelete = access.Write && access.Directory && (policy.Denied.Any(denied => SandboxPolicy.Contains(path, denied))
                || policy.Readable.Any(read => SandboxPolicy.Contains(path, read.Source)));
            var mask = access.Write ? Modify : ReadExecute;
            var inheritedMask = mask;
            if (denyDelete)
                mask &= ~Delete;
            var entry = new Entry(path, _measurements, access.Class);
            _entries.Add(entry);
            entry.Grant(_sid, mask, inheritedMask, access.Directory, denyWrite || denyDelete, policy.AllowNetwork,
                denyWrite ? effectiveRead : null, cancellationToken);
        }
        _measurements.Set(-1, 0);
    }

    private void AddTree(Dictionary<string, (bool Write, bool Directory, int Class)> paths, string root, bool directory, bool write,
        int rootClass, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _measurements.Set(0, rootClass);
        // Earlier trees already include every descendant with the same access.
        // A different access still walks the subtree to narrow all existing children.
        if (paths.TryGetValue(root, out var existing) && existing.Write == write && existing.Directory == directory)
        {
            _measurements.Set(12, rootClass);
            return;
        }
        paths[root] = (write, directory, rootClass);
        if (directory)
            foreach (var (path, isDirectory) in new FileSystemEnumerable<(string Path, bool Directory)>(root,
                static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths[path] = (write, isDirectory, rootClass);
            }
        _measurements.Set(12, rootClass);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception>? failures = null;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            try { _entries[index].Remove(_sid); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _measurements.Set(-1, 0);
        _cancellationRegistration.Dispose();
        _ = _measurements.ReportAsync(2);
        if (failures is not null)
            throw new AggregateException("Removing the AppContainer filesystem grants failed.", failures);
    }

    private sealed class AclMeasurements
    {
        private const int Stages = 13;
        // Classes are readonly=0, writable=1 and denied=2; a vector index is class * Stages + stage.
        // Stages are enumeration, ancestors, grant lock/open/read/write/label, removal lock/open/read/write/label and managed preparation.
        private static long _nextId;
        private readonly long _id = Interlocked.Increment(ref _nextId);
        private readonly long[] _ticks = new long[3 * Stages];
        private readonly long[] _counts = new long[3 * Stages];
        // Read checks: token open/duplicate failure, token ready, descriptor query failure, missing owner/group, unstable ACL,
        // current API failure/denied/allowed, no inheritable grant, label restriction, inherited API failure/denied/allowed,
        // reused file/directory and handle-open failure. Errors retain only the first native code for each check.
        private readonly long[] _readChecks = new long[17];
        private readonly int[] _readErrors = new int[17];
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _stageStarted;
        private int _current = -1;

        public void Set(int stage, int rootClass)
        {
            var now = Stopwatch.GetTimestamp();
            if (_current >= 0)
                Interlocked.Add(ref _ticks[_current], now - _stageStarted);
            Volatile.Write(ref _stageStarted, now);
            var current = stage < 0 ? -1 : rootClass * Stages + stage;
            Volatile.Write(ref _current, current);
            if (current >= 0)
                Interlocked.Increment(ref _counts[current]);
        }

        public void ReadCheck(int check, int error = 0)
        {
            Interlocked.Increment(ref _readChecks[check]);
            if (error != 0)
                Interlocked.CompareExchange(ref _readErrors[check], error, 0);
        }

        public async Task ReportAsync(int reason)
        {
            try
            {
                var current = Volatile.Read(ref _current);
                var activeMilliseconds = current < 0 ? 0 : (long)Stopwatch.GetElapsedTime(Volatile.Read(ref _stageStarted)).TotalMilliseconds;
                var milliseconds = new long[_ticks.Length];
                var counts = new long[_counts.Length];
                for (var index = 0; index < milliseconds.Length; index++)
                {
                    milliseconds[index] = (long)(Volatile.Read(ref _ticks[index]) * 1000d / Stopwatch.Frequency);
                    counts[index] = Volatile.Read(ref _counts[index]);
                }
                var readChecks = new long[_readChecks.Length];
                var readErrors = new int[_readErrors.Length];
                for (var index = 0; index < readChecks.Length; index++)
                {
                    readChecks[index] = Volatile.Read(ref _readChecks[index]);
                    readErrors[index] = Volatile.Read(ref _readErrors[index]);
                }
                // Numeric snapshots are best-effort; each lease emits its cancellation snapshot and final aggregate.
                await Console.Error.WriteLineAsync($"SANDBOX_ACL_MEASURE lease={_id} reason={reason} current={current} current-ms={activeMilliseconds} lifetime-ms={(long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds} counts={string.Join(",", counts)} completed-ms={string.Join(",", milliseconds)} read-checks={string.Join(",", readChecks)} read-errors={string.Join(",", readErrors)}").ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    private sealed class WritableLabel(byte[]? original)
    {
        public byte[]? Original { get; } = original;
        public int Owners { get; set; } = 1;
    }

    private sealed class TemporaryInheritance(List<GenericAce> copies)
    {
        public List<GenericAce> Copies { get; } = copies;
        public int Owners { get; set; } = 1;
    }

    private sealed class EffectiveRead : IDisposable
    {
        private const uint TokenQuery = 0x0008;
        private const uint TokenDuplicate = 0x0002;
        private const int SecurityImpersonation = 2;
        private const int ImpersonationToken = 2;
        private const int PrivilegeBufferBytes = 1024;
        private const uint FileGenericRead = 0x120089;
        private const uint FileGenericWrite = 0x120116;
        private const uint FileGenericExecute = 0x1200a0;
        private const uint FileAllAccess = 0x1f01ff;
        private const uint GenericAccessMask = 0xf0000000;
        private const byte MandatoryLabelAceType = 0x11;
        private const int MandatoryReadExecuteUp = 6;
        private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
        private static readonly SecurityIdentifier PrincipalSelf = new("S-1-5-10");
        private readonly SafeAccessTokenHandle _token;
        private readonly AclMeasurements _measurements;

        private EffectiveRead(SafeAccessTokenHandle token, AclMeasurements measurements)
        {
            _token = token;
            _measurements = measurements;
        }

        public static EffectiveRead? TryCreate(SafeFileHandle process, AclMeasurements measurements)
        {
            if (!WindowsNative.OpenProcessToken(process, TokenQuery | TokenDuplicate, out var primary))
            {
                measurements.ReadCheck(0, Marshal.GetLastPInvokeError());
                primary.Dispose();
                return null;
            }
            using (primary)
            {
                if (WindowsNative.DuplicateTokenEx(primary, TokenQuery, 0, SecurityImpersonation, ImpersonationToken, out var duplicate))
                {
                    measurements.ReadCheck(2);
                    return new EffectiveRead(duplicate, measurements);
                }
                measurements.ReadCheck(1, Marshal.GetLastPInvokeError());
                duplicate.Dispose();
                return null;
            }
        }

        public void Dispose() => _token.Dispose();

        public unsafe bool CanRead(nint descriptor, bool inherited = false)
        {
            var mapping = new WindowsNative.GenericMapping { Read = FileGenericRead, Write = FileGenericWrite, Execute = FileGenericExecute, All = FileAllAccess };
            byte* privileges = stackalloc byte[PrivilegeBufferBytes];
            uint length = PrivilegeBufferBytes;
            // The existing SID grant remains the fallback when the native check cannot prove read authority.
            var succeeded = WindowsNative.AccessCheck(descriptor, _token, ReadExecute, ref mapping, (nint)privileges, ref length,
                out var granted, out var allowed);
            var error = succeeded ? 0 : Marshal.GetLastPInvokeError();
            var readable = succeeded && allowed && (granted & ReadExecute) == ReadExecute;
            _measurements.ReadCheck((inherited ? 11 : 6) + (!succeeded ? 0 : readable ? 2 : 1), error);
            return readable;
        }

        public static bool IsStableAllowAcl(RawAcl acl, SecurityIdentifier sid)
        {
            foreach (GenericAce entry in acl)
                if (entry is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false } ace
                    || ace.SecurityIdentifier == sid)
                    return false;
            return true;
        }

        public unsafe bool CanInheritRead(RawAcl acl, nint owner, nint group, nint label)
        {
            // Stable OI|CI grants carry the same trustee and access mask to future files and directories.
            var inherited = new RawAcl(acl.Revision, acl.Count + 1);
            // An OWNER RIGHTS entry limits owner rights to explicit ACEs for future creators.
            inherited.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0, OwnerRights, false, null));
            var mapping = new WindowsNative.GenericMapping { Read = FileGenericRead, Write = FileGenericWrite, Execute = FileGenericExecute, All = FileAllAccess };
            foreach (CommonAce ace in acl)
            {
                const AceFlags propagation = AceFlags.ObjectInherit | AceFlags.ContainerInherit;
                if ((ace.AceFlags & (propagation | AceFlags.NoPropagateInherit)) != propagation
                    || ace.SecurityIdentifier.Value.StartsWith("S-1-3-", StringComparison.Ordinal)
                    || ace.SecurityIdentifier == PrincipalSelf)
                    continue;
                // Real inheritance maps generic rights when the ACE becomes effective on a file or directory.
                var access = unchecked((uint)ace.AccessMask);
                if ((access & GenericAccessMask) != 0)
                    WindowsNative.MapGenericMask(ref access, ref mapping);
                inherited.InsertAce(inherited.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                    unchecked((int)access), ace.SecurityIdentifier, false, null));
            }
            if (inherited.Count == 1)
            {
                _measurements.ReadCheck(9);
                return false;
            }
            if (label != 0)
            {
                var count = (ushort)Marshal.ReadInt16(label, 4);
                var offset = 8;
                for (var index = 0; index < count; index++)
                {
                    // Mandatory-label ACEs with read/execute-up restrictions retain the normal SID-grant path.
                    if (Marshal.ReadByte(label, offset) != MandatoryLabelAceType || (Marshal.ReadInt32(label, offset + 4) & MandatoryReadExecuteUp) != 0)
                    {
                        _measurements.ReadCheck(10);
                        return false;
                    }
                    offset += (ushort)Marshal.ReadInt16(label, offset + 2);
                }
            }
            var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
                new SecurityIdentifier(owner), new SecurityIdentifier(group), null, inherited);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            fixed (byte* pointer = bytes)
                return CanRead((nint)pointer, inherited: true);
        }
    }

    private sealed class Entry(string path, AclMeasurements measurements, int rootClass, bool bootstrap = false, bool bootstrapDirectory = false)
    {
        private const uint BootstrapAclAccess = 0x60000;
        private const uint PolicyAclAccess = 0xe0000;
        private const uint MaximumAllowed = 0x02000000;
        private WritableLabel? _label;
        private bool _sidGranted;
        private TemporaryInheritance? _inheritance;

        private SafeFileHandle Open(bool propagate = true)
        {
            // Bootstrap rollback needs READ_CONTROL and WRITE_DAC; final policy entries retain WRITE_OWNER for MIC restoration.
            var access = propagate ? (bootstrap ? BootstrapAclAccess : PolicyAclAccess) : MaximumAllowed;
            var handle = WindowsNative.CreateFile(path, access, 7, 0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
                return handle;
            var error = Marshal.GetLastPInvokeError();
            measurements.ReadCheck(16, error);
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open an AppContainer filesystem grant.");
        }

        public void Grant(SecurityIdentifier sid, int access, int inheritedAccess, bool directory, bool restrict, bool network,
            EffectiveRead? effectiveRead, CancellationToken cancellationToken)
        {
            // SetSecurityInfo propagates to descendants; serialize the complete read/modify/write, not just one native write, so another lease cannot overwrite or revoke this SID.
            measurements.Set(2, rootClass);
            lock (AclGate)
            {
                measurements.Set(12, rootClass);
                cancellationToken.ThrowIfCancellationRequested();
                GrantCore(sid, access, inheritedAccess, directory, restrict, network, effectiveRead);
            }
        }

        private void GrantCore(SecurityIdentifier sid, int access, int inheritedAccess, bool directory, bool restrict, bool network,
            EffectiveRead? effectiveRead)
        {
            measurements.Set(3, rootClass);
            // Only a bootstrap directory needs propagation suppressed. An executable needs ACL rights, never data-write/delete rights from MAXIMUM_ALLOWED.
            using var handle = Open(propagate: !bootstrapDirectory);
            measurements.Set(4, rootClass);
            var (acl, protectedDacl, reuseRead) = ReadGrantAcl(handle, sid, directory, effectiveRead);
            measurements.Set(12, rootClass);
            if (restrict)
                DemandNoAmbientGrant(acl, access, network);
            if (restrict && ProtectedInheritance.TryGetValue(path, out var sharedInheritance))
            {
                sharedInheritance.Owners++;
                _inheritance = sharedInheritance;
            }
            if (reuseRead && _inheritance is null)
                return;
            if (inheritedAccess != access || !HasInheritedGrant(acl, sid, access, directory))
            {
                var inheritedGrant = false;
                if (restrict)
                    for (var index = acl.Count - 1; index >= 0; index--)
                        if (acl[index] is CommonAce ace && ace.SecurityIdentifier == sid)
                        {
                            inheritedGrant |= (ace.AceFlags & AceFlags.Inherited) != 0 && (ace.AccessMask & ~access) != 0;
                            acl.RemoveAce(index);
                        }
                // Stop inheriting this SID's excess access, retaining every other principal.
                var protectDacl = inheritedGrant && !protectedDacl;
                if (protectDacl)
                {
                    _inheritance = new TemporaryInheritance(CaptureInheritanceCopies(acl));
                    ProtectedInheritance.Add(path, _inheritance);
                }
                var inherited = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
                var insertion = 0;
                while (insertion < acl.Count && (acl[insertion].AceFlags & AceFlags.Inherited) == 0)
                    insertion++;
                acl.InsertAce(insertion++, new CommonAce(inheritedAccess == access ? inherited : AceFlags.None, AceQualifier.AccessAllowed, access, sid, false, null));
                if (directory && inheritedAccess != access)
                    acl.InsertAce(insertion, new CommonAce(inherited | AceFlags.InheritOnly, AceQualifier.AccessAllowed, inheritedAccess, sid, false, null));
                _sidGranted = true;
                measurements.Set(5, rootClass);
                WriteAcl(handle, acl, protectDacl ? ProtectDacl : 0);
                measurements.Set(12, rootClass);
            }
            if ((access & WriteData) == 0)
                return;
            if (WritableLabels.TryGetValue(path, out var shared))
            {
                shared.Owners++;
                _label = shared;
                return;
            }
            measurements.Set(6, rootClass);
            var originalLabel = ReadAclBytes(handle, 0x10).Acl;
            if (!WindowsNative.ConvertStringSecurityDescriptorToSecurityDescriptor(directory ? "S:(ML;OICI;NW;;;LW)" : "S:(ML;;NW;;;LW)", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot construct the AppContainer writable label.");
            try
            {
                if (!WindowsNative.GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer writable label.");
                Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, sacl));
                _label = new WritableLabel(originalLabel);
                WritableLabels.Add(path, _label);
            }
            finally { WindowsNative.LocalFree(descriptor); }
            measurements.Set(12, rootClass);
        }

        private static bool HasInheritedGrant(RawAcl acl, SecurityIdentifier sid, int access, bool directory)
        {
            var propagation = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
            for (var index = 0; index < acl.Count; index++)
                if (acl[index] is CommonAce ace && ace.AceQualifier == AceQualifier.AccessAllowed
                    && ace.SecurityIdentifier == sid && ace.AccessMask == access
                    && (ace.AceFlags & (AceFlags.Inherited | AceFlags.InheritOnly | AceFlags.NoPropagateInherit)) == AceFlags.Inherited
                    && (ace.AceFlags & propagation) == propagation)
                    return true;
            return false;
        }

        public static void DemandAmbientAncestorGrant(string path, bool network)
        {
            using var handle = WindowsNative.CreateFile(path, 0x20000, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect an AppContainer filesystem grant ancestor.");
            DemandNoAmbientGrant(ReadAcl(handle).Acl, ReadExecute, network);
        }

        private static void DemandNoAmbientGrant(RawAcl acl, int access, bool network)
        {
            const uint genericAll = 0x10000000, genericWrite = 0x40000000, fileAccessBits = 0x1fffff;
            foreach (GenericAce entry in acl)
            {
                if (entry is not QualifiedAce { AceQualifier: AceQualifier.AccessAllowed } ace)
                    continue;
                var principal = ace.SecurityIdentifier;
                if (principal != AllApplicationPackages && principal != AllRestrictedApplicationPackages
                    && (!network || !NetworkCapabilitySids.Contains(principal)))
                    continue;
                var mask = unchecked((uint)ace.AccessMask);
                if ((mask & fileAccessBits & ~(uint)access) != 0 || (mask & genericAll) != 0
                    || (mask & genericWrite) != 0 && (access & WriteData) == 0)
                    throw new NotSupportedException("An existing AppContainer group or enabled capability grant prevents enforcing the protected filesystem path.");
            }
        }

        private static List<GenericAce> CaptureInheritanceCopies(RawAcl acl)
        {
            List<GenericAce> copies = [];
            foreach (GenericAce ace in acl)
            {
                if ((ace.AceFlags & AceFlags.Inherited) == 0)
                    continue;
                var copy = ace.Copy();
                copy.AceFlags &= ~AceFlags.Inherited;
                copies.Add(copy);
            }
            return copies;
        }

        private static void RemoveInheritanceCopies(RawAcl acl, List<GenericAce> copies)
        {
            // Protecting the DACL materializes inherited ACEs as explicit copies.
            // Remove only unchanged copies before inheriting the current parent ACL again.
            foreach (var copy in copies)
                for (var index = 0; index < acl.Count; index++)
                    if (copy.Equals(acl[index]))
                    {
                        acl.RemoveAce(index);
                        break;
                    }
        }

        public void Remove(SecurityIdentifier sid)
        {
            measurements.Set(7, rootClass);
            lock (AclGate)
                RemoveCore(sid);
        }

        private unsafe void RemoveCore(SecurityIdentifier sid)
        {
            measurements.Set(12, rootClass);
            var restoreInheritance = false;
            var copies = _inheritance?.Copies;
            if (_inheritance is { } inherited)
            {
                restoreInheritance = --inherited.Owners == 0;
                if (restoreInheritance)
                    ProtectedInheritance.Remove(path);
                _inheritance = null;
            }
            var restoreLabel = false;
            var originalLabel = _label?.Original;
            if (_label is { } owned)
            {
                restoreLabel = --owned.Owners == 0;
                if (restoreLabel)
                    WritableLabels.Remove(path);
                _label = null;
            }
            if ((!_sidGranted && !restoreLabel && !restoreInheritance) || !File.Exists(path) && !Directory.Exists(path))
                return;
            measurements.Set(8, rootClass);
            using var handle = Open();
            if (_sidGranted || restoreInheritance)
            {
                measurements.Set(9, rootClass);
                var acl = ReadAcl(handle).Acl;
                measurements.Set(12, rootClass);
                var removedGrant = false;
                for (var index = acl.Count - 1; index >= 0; index--)
                    if (acl[index] is KnownAce ace && ace.SecurityIdentifier == sid)
                    {
                        acl.RemoveAce(index);
                        removedGrant = true;
                    }
                if (restoreInheritance)
                    RemoveInheritanceCopies(acl, copies!);
                if (removedGrant || restoreInheritance)
                {
                    measurements.Set(10, rootClass);
                    WriteAcl(handle, acl, restoreInheritance ? UnprotectDacl : 0);
                    measurements.Set(12, rootClass);
                }
            }
            if (restoreLabel)
            {
                measurements.Set(11, rootClass);
                fixed (byte* label = originalLabel)
                    Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, (nint)label));
                measurements.Set(12, rootClass);
            }
        }

        private (RawAcl Acl, bool Protected, bool ReuseRead) ReadGrantAcl(SafeFileHandle handle,
            SecurityIdentifier sid, bool directory, EffectiveRead? effectiveRead)
        {
            if (effectiveRead is null)
            {
                var original = ReadAcl(handle);
                return (original.Acl, original.Protected, false);
            }
            var queryError = WindowsNative.GetSecurityInfo(handle, 1, 0x17, out var owner, out var group,
                out var dacl, out var label, out var descriptor);
            if (queryError != 0)
            {
                measurements.ReadCheck(3, unchecked((int)queryError));
                var original = ReadAcl(handle);
                return (original.Acl, original.Protected, false);
            }
            try
            {
                if (!WindowsNative.GetSecurityDescriptorControl(descriptor, out var control, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer filesystem grant inheritance.");
                if (dacl == 0)
                    throw new NotSupportedException("An AppContainer filesystem grant requires an explicit ACL.");
                var bytes = new byte[(ushort)Marshal.ReadInt16(dacl, 2)];
                Marshal.Copy(dacl, bytes, 0, bytes.Length);
                var acl = new RawAcl(bytes, 0);
                var protectedDacl = (control & DaclProtected) != 0;
                // Inspect every object, including protected descendants, and retain the narrowing path for this SID's existing ACEs.
                var hasPrincipals = owner != 0 && group != 0;
                var stable = EffectiveRead.IsStableAllowAcl(acl, sid);
                if (!hasPrincipals)
                    measurements.ReadCheck(4);
                else if (!stable)
                    measurements.ReadCheck(5);
                var reuseRead = hasPrincipals && stable && effectiveRead.CanRead(descriptor)
                    && (!directory || effectiveRead.CanInheritRead(acl, owner, group, label));
                if (reuseRead)
                    measurements.ReadCheck(directory ? 15 : 14);
                return (acl, protectedDacl, reuseRead);
            }
            finally { WindowsNative.LocalFree(descriptor); }
        }

        private static (RawAcl Acl, bool Protected) ReadAcl(SafeFileHandle handle)
        {
            var (bytes, control) = ReadAclBytes(handle, 4);
            return (new RawAcl(bytes ?? throw new NotSupportedException("An AppContainer filesystem grant requires an explicit ACL."), 0),
                (control & DaclProtected) != 0);
        }

        private static (byte[]? Acl, ushort Control) ReadAclBytes(SafeFileHandle handle, uint information)
        {
            ushort control = 0;
            Check(WindowsNative.GetSecurityInfo(handle, 1, information, out _, out _, out var dacl, out var sacl, out var descriptor));
            try
            {
                if (information == 4 && !WindowsNative.GetSecurityDescriptorControl(descriptor, out control, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer filesystem grant inheritance.");
                var pointer = information == 4 ? dacl : sacl;
                if (pointer == 0)
                    return (null, control);
                var bytes = new byte[(ushort)Marshal.ReadInt16(pointer, 2)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return (bytes, control);
            }
            finally { WindowsNative.LocalFree(descriptor); }
        }

        private static unsafe void WriteAcl(SafeFileHandle handle, RawAcl acl, uint inheritance)
        {
            var bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            fixed (byte* pointer = bytes)
                Check(WindowsNative.SetSecurityInfo(handle, 1, 4 | inheritance, 0, 0, (nint)pointer, 0));
        }

        private static void Check(uint error)
        {
            if (error != 0)
                throw new Win32Exception((int)error, "Cannot apply or remove the AppContainer filesystem grant.");
        }
    }
}
