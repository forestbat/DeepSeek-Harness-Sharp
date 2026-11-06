using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Leases declared root access for an AppContainer SID and restores changes on disposal.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAclLease : IDisposable
{
    private const int ReadExecute = 0x1200a9;
    private const int Modify = 0x1301bf;
    private const int Delete = 0x10000;
    private const int DeleteChild = 0x40;
    private const int WriteAccess = (Modify & ~ReadExecute) | DeleteChild;
    private static readonly Lock AclGate = new();
    private static readonly Dictionary<string, WritableLabel> WritableLabels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SecurityIdentifier RestrictedPackages = new("S-1-15-2-2");
    private readonly List<Entry> _entries = [];
    private readonly SecurityIdentifier _sid;
    private bool _disposed;

    private WindowsAclLease(SecurityIdentifier sid) => _sid = sid;

    public static WindowsAclLease CreateBootstrap(nint sid, SandboxPolicy policy, CancellationToken cancellationToken)
    {
        var lease = new WindowsAclLease(new SecurityIdentifier(sid));
        try
        {
            // Bootstrap grants apply only to the executable and working-directory objects before the suspended process exists.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            GrantObject(policy.Executable);
            if (!StringComparer.OrdinalIgnoreCase.Equals(policy.Executable, policy.WorkingDirectory))
                GrantObject(policy.WorkingDirectory);
            return lease;

            void GrantObject(string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SandboxPolicy.Contains(windows, path))
                    return;
                lease.Grant(path, ReadExecute, 0, false, false, cancellationToken);
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

    public void Apply(SandboxPolicy policy, CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var grant in policy.Writable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SandboxPolicy.Contains(windows, grant.Source))
                Grant(grant.Source, Modify, 0, grant.IsDirectory, true, cancellationToken);
        }
        foreach (var grant in policy.Readable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SandboxPolicy.Contains(windows, grant.Source))
                Grant(grant.Source, ReadExecute, WriteAccess, grant.IsDirectory, false, cancellationToken);
            ProtectAncestors(grant.Source);
        }
        foreach (var denied in policy.Denied)
            ProtectAncestors(denied);
        foreach (var denied in policy.Denied)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(denied) || Directory.Exists(denied))
                Grant(denied, ReadExecute, WriteAccess, Directory.Exists(denied), false, cancellationToken);
            else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
        }

        void ProtectAncestors(string protectedPath)
        {
            foreach (var write in policy.Writable)
            {
                if (!SandboxPolicy.Contains(write.Source, protectedPath))
                    continue;
                // Protect only named ancestors from rename or deletion without enumerating their children.
                for (var parent = Path.GetDirectoryName(protectedPath); parent is not null && SandboxPolicy.Contains(write.Source, parent);
                     parent = Path.GetDirectoryName(parent))
                    Grant(parent, 0, Delete | DeleteChild, false, false, cancellationToken);
            }
        }
    }

    private void Grant(string path, int access, int deniedAccess, bool directory, bool writable, CancellationToken cancellationToken)
    {
        var entry = new Entry(path);
        _entries.Add(entry);
        entry.Grant(_sid, access, deniedAccess, directory, writable, cancellationToken);
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

    private sealed class Entry(string path)
    {
        private const uint ProtectDacl = 0x80000000, InheritDacl = 0x20000000;
        private WritableLabel? _label;
        private bool _sidGranted;
        private RawAcl? _unprotectedAcl;

        public void Grant(SecurityIdentifier sid, int access, int deniedAccess, bool directory, bool writable,
            CancellationToken cancellationToken)
        {
            // Serialize native read/modify/write so concurrent leases retain each other's SID grants.
            lock (AclGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = WindowsNative.OpenSecurityHandle(path, writable);
                var acl = ReadAcl(handle);
                // LPAC excludes ALL APPLICATION PACKAGES; reuse only existing restricted-package read inheritance.
                if (!writable && directory && access == ReadExecute && HasRestrictedPackageReadAccess(acl, sid))
                    return;
                acl = SetSidAccess(acl, sid, access, deniedAccess, directory);
                _sidGranted = true;
                // Windows propagates inheritable ACEs; protected descendants can still reject actual process access.
                WriteAcl(handle, acl, _unprotectedAcl is not null ? ProtectDacl : 0);
                if (!writable)
                    return;
                if (WritableLabels.TryGetValue(path, out var shared))
                {
                    shared.Owners++;
                    _label = shared;
                    return;
                }
                var originalLabel = ReadAclBytes(handle, 0x10);
                nint descriptor = 0;
                try
                {
                    if (!WindowsNative.ConvertStringSecurityDescriptorToSecurityDescriptor(directory ? "S:(ML;OICI;NW;;;LW)" : "S:(ML;;NW;;;LW)", 1, out descriptor, out _))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot construct the AppContainer writable label.");
                    if (!WindowsNative.GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer writable label.");
                    _label = new WritableLabel(originalLabel);
                    WritableLabels.Add(path, _label);
                    WriteLabel(handle, sacl);
                }
                finally
                {
                    if (descriptor != 0)
                        WindowsNative.LocalFree(descriptor);
                }
            }
        }

        private static bool HasRestrictedPackageReadAccess(RawAcl acl, SecurityIdentifier sid)
        {
            const uint genericRead = 0x80000000, genericExecute = 0x20000000;
            const uint allowedReadMask = ReadExecute | genericRead | genericExecute;
            const uint fileGenericRead = 0x120089, fileGenericExecute = 0x1200a0;
            uint self = 0, files = 0, directories = 0;
            for (var index = 0; index < acl.Count; index++)
            {
                if (acl[index] is not CommonAce ace || ace.IsCallback || ace.AceQualifier != AceQualifier.AccessAllowed
                    || ace.SecurityIdentifier == sid)
                    return false;
                if (ace.SecurityIdentifier != RestrictedPackages)
                    continue;
                var mask = unchecked((uint)ace.AccessMask);
                if ((mask & ~allowedReadMask) != 0)
                    return false;
                if ((mask & genericRead) != 0)
                    mask |= fileGenericRead;
                if ((mask & genericExecute) != 0)
                    mask |= fileGenericExecute;
                if ((ace.AceFlags & AceFlags.InheritOnly) == 0)
                    self |= mask;
                if ((ace.AceFlags & AceFlags.NoPropagateInherit) != 0)
                    continue;
                if ((ace.AceFlags & AceFlags.ObjectInherit) != 0)
                    files |= mask;
                if ((ace.AceFlags & AceFlags.ContainerInherit) != 0)
                    directories |= mask;
            }
            return (self & files & directories & ReadExecute) == ReadExecute;
        }

        private RawAcl SetSidAccess(RawAcl acl, SecurityIdentifier sid, int access, int deniedAccess, bool directory)
        {
            var objectAccess = access;
            var childAccess = directory ? access : 0;
            var protectInheritance = false;
            const AceFlags inherit = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
            for (var index = acl.Count - 1; index >= 0; index--)
                if (acl[index] is CommonAce ace && ace.SecurityIdentifier == sid)
                {
                    if (ace.AceQualifier == AceQualifier.AccessAllowed)
                    {
                        if ((ace.AceFlags & AceFlags.InheritOnly) == 0)
                            objectAccess |= ace.AccessMask;
                        if ((ace.AceFlags & inherit) != 0)
                            childAccess |= ace.AccessMask;
                        protectInheritance |= (ace.AceFlags & AceFlags.Inherited) != 0 && (ace.AccessMask & deniedAccess) != 0;
                    }
                    acl.RemoveAce(index);
                }
            if (protectInheritance)
            {
                _unprotectedAcl = acl;
                acl = new RawAcl(acl.Revision, acl.Count + 2);
                for (var index = 0; index < _unprotectedAcl.Count; index++)
                    acl.InsertAce(index, _unprotectedAcl[index]);
            }
            objectAccess &= ~deniedAccess;
            if (directory)
                childAccess &= ~deniedAccess;
            var insertion = 0;
            while (insertion < acl.Count && (acl[insertion].AceFlags & AceFlags.Inherited) == 0)
                insertion++;
            // AppContainer checks accumulate allow ACEs, so restrictions reduce this SID's actual allow masks.
            if (objectAccess == childAccess && objectAccess != 0)
                acl.InsertAce(insertion, new CommonAce(inherit, AceQualifier.AccessAllowed, objectAccess, sid, false, null));
            else
            {
                if (objectAccess != 0)
                    acl.InsertAce(insertion++, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, objectAccess, sid, false, null));
                if (childAccess != 0)
                    acl.InsertAce(insertion, new CommonAce(inherit | AceFlags.InheritOnly, AceQualifier.AccessAllowed, childAccess, sid, false, null));
            }
            return acl;
        }

        private void RestoreInheritedAces(RawAcl acl)
        {
            if (_unprotectedAcl is not { } original)
                return;
            // Protecting the declared object's DACL converted inherited ACEs into explicit ACEs.
            for (var index = 0; index < original.Count; index++)
            {
                if ((original[index].AceFlags & AceFlags.Inherited) == 0)
                    continue;
                var converted = original[index].Copy();
                converted.AceFlags &= ~AceFlags.Inherited;
                var retainedExplicit = 0;
                for (var before = 0; before < original.Count; before++)
                    if (original[before].Equals(converted))
                        retainedExplicit++;
                for (var current = acl.Count - 1; current >= 0; current--)
                    if (acl[current].Equals(converted) && retainedExplicit-- <= 0)
                    {
                        acl.RemoveAce(current);
                        break;
                    }
            }
        }

        public unsafe void Remove(SecurityIdentifier sid)
        {
            lock (AclGate)
            {
                var restoreLabel = false;
                var originalLabel = _label?.Original;
                if (_label is { } owned)
                {
                    restoreLabel = --owned.Owners == 0;
                    if (restoreLabel)
                        WritableLabels.Remove(path);
                    _label = null;
                }
                if ((!_sidGranted && !restoreLabel) || !File.Exists(path) && !Directory.Exists(path))
                    return;
                using var handle = WindowsNative.OpenSecurityHandle(path, restoreLabel);
                if (_sidGranted)
                {
                    var acl = ReadAcl(handle);
                    var removedGrant = false;
                    for (var index = acl.Count - 1; index >= 0; index--)
                        if (acl[index] is KnownAce ace && ace.SecurityIdentifier == sid)
                        {
                            acl.RemoveAce(index);
                            removedGrant = true;
                        }
                    if (removedGrant || _unprotectedAcl is not null)
                    {
                        RestoreInheritedAces(acl);
                        WriteAcl(handle, acl, _unprotectedAcl is not null ? InheritDacl : 0);
                    }
                }
                if (restoreLabel)
                {
                    fixed (byte* label = originalLabel)
                        WriteLabel(handle, (nint)label);
                }
            }
        }

        private static RawAcl ReadAcl(SafeFileHandle handle)
            => new(ReadAclBytes(handle, 4) ?? throw new NotSupportedException("An AppContainer filesystem grant requires an explicit ACL."), 0);

        private static byte[]? ReadAclBytes(SafeFileHandle handle, uint information)
        {
            nint descriptor = 0;
            try
            {
                Check(WindowsNative.GetSecurityInfo(handle, 1, information, out _, out _, out var dacl, out var sacl, out descriptor));
                var pointer = information == 4 ? dacl : sacl;
                if (pointer == 0)
                    return null;
                var bytes = new byte[(ushort)Marshal.ReadInt16(pointer, 2)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (descriptor != 0)
                    WindowsNative.LocalFree(descriptor);
            }
        }

        private static unsafe void WriteAcl(SafeFileHandle handle, RawAcl acl, uint flags = 0)
        {
            var bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            fixed (byte* pointer = bytes)
                Check(WindowsNative.SetSecurityInfo(handle, 1, 4 | flags, 0, 0, (nint)pointer, 0));
        }

        private static void WriteLabel(SafeFileHandle handle, nint label)
            => Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, label));

        private static void Check(uint error)
        {
            if (error != 0)
                throw new Win32Exception((int)error, "Cannot apply or remove the AppContainer filesystem grant.");
        }
    }
}
