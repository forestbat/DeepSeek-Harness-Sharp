using System.ComponentModel;
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
    private bool _disposed;

    private WindowsAclLease(SecurityIdentifier sid) => _sid = sid;

    public static WindowsAclLease Create(nint sid, SandboxPolicy policy, CancellationToken cancellationToken)
    {
        var lease = new WindowsAclLease(new SecurityIdentifier(sid));
        try
        {
            var paths = new Dictionary<string, (bool Write, bool Directory)>(StringComparer.OrdinalIgnoreCase);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var grant in policy.Writable.OrderBy(static grant => grant.Source.Length))
                if (!SandboxPolicy.Contains(windows, grant.Source))
                    AddTree(paths, grant.Source, grant.IsDirectory, true, cancellationToken);
            foreach (var grant in policy.Readable.OrderBy(static grant => grant.Source.Length))
                if (!SandboxPolicy.Contains(windows, grant.Source))
                    AddTree(paths, grant.Source, grant.IsDirectory, false, cancellationToken);
            foreach (var denied in policy.Denied)
            {
                if (File.Exists(denied) || Directory.Exists(denied))
                    AddTree(paths, denied, Directory.Exists(denied), false, cancellationToken);
                else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                    throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
            }
            var checkedAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Keys)
                for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (paths.ContainsKey(parent) || !checkedAncestors.Add(parent))
                        break;
                    Entry.DemandAmbientAncestorGrant(parent, policy.AllowNetwork);
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
                var entry = new Entry(path);
                lease._entries.Add(entry);
                entry.Grant(lease._sid, mask, inheritedMask, access.Directory, denyWrite || denyDelete, policy.AllowNetwork, cancellationToken);
            }
            return lease;
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

    private static void AddTree(Dictionary<string, (bool Write, bool Directory)> paths, string root, bool directory, bool write,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Earlier trees already include every descendant with the same access.
        // A different access still walks the subtree to narrow all existing children.
        if (paths.TryGetValue(root, out var existing) && existing == (write, directory))
            return;
        paths[root] = (write, directory);
        if (directory)
            foreach (var (path, isDirectory) in new FileSystemEnumerable<(string Path, bool Directory)>(root,
                static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths[path] = (write, isDirectory);
            }
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
        if (failures is not null)
            throw new AggregateException("Removing the AppContainer filesystem grants failed.", failures);
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

    private sealed class Entry(string path)
    {
        private WritableLabel? _label;
        private bool _sidGranted;
        private TemporaryInheritance? _inheritance;

        private SafeFileHandle Open()
        {
            var handle = WindowsNative.CreateFile(path, 0xe0000, 7, 0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
                return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open an AppContainer filesystem grant.");
        }

        public void Grant(SecurityIdentifier sid, int access, int inheritedAccess, bool directory, bool restrict, bool network,
            CancellationToken cancellationToken)
        {
            // SetSecurityInfo propagates to descendants; serialize the complete read/modify/write, not just one native write, so another lease cannot overwrite or revoke this SID.
            lock (AclGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GrantCore(sid, access, inheritedAccess, directory, restrict, network);
            }
        }

        private void GrantCore(SecurityIdentifier sid, int access, int inheritedAccess, bool directory, bool restrict, bool network)
        {
            using var handle = Open();
            var (acl, protectedDacl) = ReadAcl(handle);
            if (restrict)
                DemandNoAmbientGrant(acl, access, network);
            if (restrict && ProtectedInheritance.TryGetValue(path, out var sharedInheritance))
            {
                sharedInheritance.Owners++;
                _inheritance = sharedInheritance;
            }
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
                WriteAcl(handle, acl, protectDacl ? ProtectDacl : 0);
            }
            if ((access & WriteData) == 0)
                return;
            if (WritableLabels.TryGetValue(path, out var shared))
            {
                shared.Owners++;
                _label = shared;
                return;
            }
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
            lock (AclGate)
                RemoveCore(sid);
        }

        private unsafe void RemoveCore(SecurityIdentifier sid)
        {
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
            using var handle = Open();
            if (_sidGranted || restoreInheritance)
            {
                var acl = ReadAcl(handle).Acl;
                for (var index = acl.Count - 1; index >= 0; index--)
                    if (acl[index] is KnownAce ace && ace.SecurityIdentifier == sid)
                        acl.RemoveAce(index);
                if (restoreInheritance)
                    RemoveInheritanceCopies(acl, copies!);
                WriteAcl(handle, acl, restoreInheritance ? UnprotectDacl : 0);
            }
            if (restoreLabel)
                fixed (byte* label = originalLabel)
                    Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, (nint)label));
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
