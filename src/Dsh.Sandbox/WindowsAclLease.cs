using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Leases only a unique AppContainer SID and pins host restoration authority before changing object security.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAclLease : IDisposable
{
    private const int ReadExecute = 0x1200a9;
    private const int Modify = 0x1301bf;
    private const int ChangeSecurity = 0xc0000;
    private const int ProtectedWrite = 0xd0156;
    private const int DeleteAndDeleteChild = 0x10040;
    private static readonly Lock RegistryGate = new();
    private static readonly List<string> ActiveWritableRoots = [];
    private static readonly List<WindowsAclLease> FailedLeases = [];
    private static readonly HashSet<(ulong Device, ulong Inode)> FailedObjects = [];
    private readonly List<Entry> _entries = [];
    private readonly SecurityIdentifier _sid;
    private readonly SandboxPolicy _policy;
    private readonly Lock _disposeGate = new();
    private AggregateException? _cleanupFailure;
    private bool _disposed;

    private WindowsAclLease(SecurityIdentifier sid, SandboxPolicy policy) { _sid = sid; _policy = policy; }

    public static WindowsAclLease Create(nint sid, SandboxPolicy policy)
    {
        // A protected creation-time DACL can exclude inherited ACEs and revoke the ordinary-user host's owner rights.
        // Only brokered creation can retain restoration handles before an untrusted creator chooses that descriptor.
        if (policy.Writable.Any(static path => path.IsDirectory))
            throw new NotSupportedException("Windows direct writable-directory sandboxes require independent authority to restore hostile creation-time DACLs; use a read-only worker with host broker writes.");
        var lease = new WindowsAclLease(new SecurityIdentifier(sid), policy);
        lock (RegistryGate)
        {
            if (policy.Writable.Any(write => ActiveWritableRoots.Any(active => SandboxPolicy.Contains(active, write.Source) || SandboxPolicy.Contains(write.Source, active))))
                throw new NotSupportedException("Concurrent Windows sandboxes cannot lease overlapping writable trees; serialize those operations or use the host broker.");
            ActiveWritableRoots.AddRange(policy.Writable.Select(static path => path.Source));
        }
        try
        {
            var paths = new Dictionary<string, (bool Write, bool Directory)>(StringComparer.OrdinalIgnoreCase);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var grant in policy.Writable.Concat(policy.Readable))
            {
                // LPAC retains the OS-provisioned restricted-application runtime ACLs, not all-application-package user grants.
                if (SandboxPolicy.Contains(windows, grant.Source))
                    continue;
                AddTree(paths, grant.Source, grant.IsDirectory, policy.Writable.Contains(grant));
            }
            foreach (var denied in policy.Denied.Where(File.Exists))
                paths.TryAdd(denied, (false, false));
            foreach (var (path, access) in paths)
            {
                var protectedFile = policy.Denied.Any(denied => string.Equals(denied, path, StringComparison.OrdinalIgnoreCase)) || policy.Readable.Any(read => SandboxPolicy.Contains(read.Source, path));
                var protectedAncestor = access.Directory && policy.Denied.Concat(policy.Readable.Select(static read => read.Source)).Any(denied => SandboxPolicy.Contains(path, denied));
                var entry = Entry.Open(path);
                lease._entries.Add(entry);
                entry.Grant(lease._sid, access.Write, access.Directory, protectedFile, protectedAncestor);
            }
            return lease;
        }
        catch (Exception creationFailure)
        {
            try { lease.Dispose(); }
            catch (Exception restorationFailure)
            {
                throw new AggregateException("Creating and restoring the AppContainer filesystem lease failed.", creationFailure, restorationFailure);
            }
            throw;
        }
    }

    private static void AddTree(Dictionary<string, (bool Write, bool Directory)> paths, string root, bool directory, bool write)
    {
        paths[root] = (write, directory);
        if (!directory)
            return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            if (paths.Count >= 32768)
                throw new NotSupportedException("Windows ACL leases are bounded to 32768 objects; use narrower runtime and workspace grants.");
            var attributes = File.GetAttributes(path);
            paths[path] = (write, (attributes & FileAttributes.Directory) != 0);
        }
    }

    public void RetainFailure(Exception failure)
    {
        lock (_disposeGate)
        {
            if (_cleanupFailure is not null)
                return;
            lock (RegistryGate)
            {
                FailedLeases.Add(this);
                foreach (var entry in _entries)
                    FailedObjects.Add((entry.Identity.Device, entry.Identity.Inode));
                _cleanupFailure = new AggregateException("The Windows sandbox filesystem lease could not be restored; its reservations remain held.", failure);
            }
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_cleanupFailure is not null)
                throw _cleanupFailure;
            if (_disposed)
                return;
            List<Exception>? failures = null;
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var entry = _entries[index];
                try { entry.RemoveSid(_sid); entry.RestoreLabel(entry.OriginalLabel); entry.Dispose(); }
                catch (Exception exception)
                {
                    lock (RegistryGate)
                        FailedObjects.Add((entry.Identity.Device, entry.Identity.Inode));
                    (failures ??= []).Add(exception);
                }
            }
            lock (RegistryGate)
            {
                if (failures is not null)
                {
                    // Keep reservations and restoration handles alive until an explicit recovery mechanism exists.
                    FailedLeases.Add(this);
                    _cleanupFailure = new AggregateException("Restoring the AppContainer filesystem lease failed; its reservations remain held.", failures);
                    throw _cleanupFailure;
                }
                foreach (var path in _policy.Writable)
                    ActiveWritableRoots.Remove(path.Source);
            }
            _disposed = true;
        }
    }

    private sealed class Entry : IDisposable
    {
        private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
        private static readonly Dictionary<(ulong Device, ulong Inode), OwnerRightsState> OwnerLeases = [];
        private static readonly HashSet<(ulong Device, ulong Inode)> WritableObjects = [];
        public byte[]? OriginalLabel { get; private set; }
        public NativeFileIdentity Identity { get; }
        private bool _labelChanged;
        private bool _ownerRightsHeld, _writeHeld, _sidGranted;
        private readonly SafeFileHandle _handle;
        private Entry(SafeFileHandle handle) { _handle = handle; Identity = NativeFileIdentity.Get(handle); }

        public static Entry Open(string path)
        {
            var handle = WindowsNative.CreateFile(path, 0xe0000, 7, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot pin a Windows sandbox filesystem object for its ACL lease.");
            }
            try { return new Entry(handle); }
            catch { handle.Dispose(); throw; }
        }

        public void Grant(SecurityIdentifier sid, bool write, bool directory, bool protectedFile, bool protectedAncestor)
        {
            lock (RegistryGate)
            {
                if (FailedObjects.Contains((Identity.Device, Identity.Inode)))
                    throw new NotSupportedException("A previous Windows filesystem lease could not restore this native object; further leases are refused.");
                AcquireOwnerRights();
                var acl = ReadAcl(4);
                var inherited = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
                acl.InsertAce(0, new CommonAce(inherited, AceQualifier.AccessDenied, ChangeSecurity, sid, false, null));
                if (protectedFile)
                    acl.InsertAce(0, new CommonAce(inherited, AceQualifier.AccessDenied, ProtectedWrite, sid, false, null));
                if (protectedAncestor)
                    acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, DeleteAndDeleteChild, sid, false, null));
                acl.InsertAce(acl.Count, new CommonAce(inherited, AceQualifier.AccessAllowed, write && !protectedFile ? Modify : ReadExecute, sid, false, null));
                _sidGranted = true;
                WriteAcl(acl);
                if (write && !protectedFile)
                {
                    if (!WritableObjects.Add((Identity.Device, Identity.Inode)))
                        throw new NotSupportedException("Concurrent Windows writable leases alias the same native filesystem object.");
                    _writeHeld = true;
                    OriginalLabel = ReadAclBytes(0x10);
                    SetLowLabel();
                }
            }
        }

        private void AcquireOwnerRights()
        {
            var key = (Identity.Device, Identity.Inode);
            if (OwnerLeases.TryGetValue(key, out var current))
            {
                current.References++;
                _ownerRightsHeld = true;
                return;
            }
            var acl = ReadAcl(4);
            var added = !acl.Cast<GenericAce>().Any(ace => (ace.AceFlags & AceFlags.Inherited) == 0 && ace is KnownAce known && known.SecurityIdentifier == OwnerRights);
            if (added)
            {
                // Presence of OWNER RIGHTS suppresses implicit owner WRITE_DAC; explicit host-user ACL grants are preserved.
                acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x20000, OwnerRights, false, null));
                WriteAcl(acl);
            }
            OwnerLeases.Add(key, new OwnerRightsState(added));
            _ownerRightsHeld = true;
        }

        private sealed class OwnerRightsState(bool added)
        {
            public int References = 1;
            public bool Added { get; } = added;
        }

        private RawAcl ReadAcl(uint information)
        {
            var bytes = ReadAclBytes(information);
            if (bytes is null)
                throw new NotSupportedException("A sandbox filesystem object has no explicit ACL; an unrestricted null DACL cannot be leased safely.");
            return new RawAcl(bytes, 0);
        }

        private byte[]? ReadAclBytes(uint information)
        {
            var result = WindowsNative.GetSecurityInfo(_handle, 1, information, out _, out _, out var dacl, out var sacl, out var descriptor);
            if (result != 0)
                throw new Win32Exception((int)result, "Cannot read a sandbox filesystem security descriptor.");
            try
            {
                var pointer = information == 4 ? dacl : sacl;
                if (pointer == 0)
                    return null;
                var bytes = new byte[(ushort)Marshal.ReadInt16(pointer, 2)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { WindowsNative.LocalFree(descriptor); }
        }

        public void RemoveSid(SecurityIdentifier sid)
        {
            lock (RegistryGate)
            {
                if (!_sidGranted)
                    return;
                var acl = ReadAcl(4);
                for (var index = acl.Count - 1; index >= 0; index--)
                    if (acl[index] is KnownAce ace && ace.SecurityIdentifier == sid)
                        acl.RemoveAce(index);
                WriteAcl(acl);
                _sidGranted = false;
            }
        }

        private unsafe void WriteAcl(RawAcl acl)
        {
            Check(WindowsNative.GetSecurityInfo(_handle, 1, 4, out _, out _, out _, out _, out var current));
            ControlFlags control;
            try
            {
                const ControlFlags daclFlags = ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.DiscretionaryAclDefaulted | ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired;
                control = (ControlFlags)(ushort)Marshal.ReadInt16(current, 2) & daclFlags;
            }
            finally { WindowsNative.LocalFree(current); }
            var descriptor = new RawSecurityDescriptor(control | ControlFlags.DiscretionaryAclPresent, null, null, null, acl);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            // SetSecurityInfo would recursively propagate inheritable ACEs through existing children.
            // The native per-handle operation changes only this pinned object; existing children are leased explicitly without following reparse points.
            fixed (byte* pointer = bytes)
            {
                var status = WindowsNative.NtSetSecurityObject(_handle, 4, (nint)pointer);
                if (status < 0)
                    Check(WindowsNative.RtlNtStatusToDosError(status));
            }
        }

        private void SetLowLabel()
        {
            if (!WindowsNative.ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;LW)", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot construct the AppContainer writable integrity label.");
            try
            {
                if (!WindowsNative.GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer integrity label.");
                Check(WindowsNative.SetSecurityInfo(_handle, 1, 0x10, 0, 0, 0, sacl));
                _labelChanged = true;
            }
            finally { WindowsNative.LocalFree(descriptor); }
        }

        public unsafe void RestoreLabel(byte[]? label)
        {
            if (!_labelChanged)
                return;
            fixed (byte* pointer = label)
                Check(WindowsNative.SetSecurityInfo(_handle, 1, 0x10, 0, 0, 0, (nint)pointer));
            _labelChanged = false;
        }

        private static void Check(uint error)
        {
            if (error != 0)
                throw new Win32Exception((int)error, "Cannot apply or restore the AppContainer filesystem security lease.");
        }
        public void Dispose()
        {
            lock (RegistryGate)
            {
                var key = (Identity.Device, Identity.Inode);
                if (_ownerRightsHeld)
                {
                    var state = OwnerLeases[key];
                    if (state.References == 1)
                    {
                        if (state.Added)
                        {
                            var acl = ReadAcl(4);
                            for (var index = acl.Count - 1; index >= 0; index--)
                                if (acl[index] is CommonAce ace && ace.SecurityIdentifier == OwnerRights && ace.AceFlags == AceFlags.None && ace.AceQualifier == AceQualifier.AccessAllowed && ace.AccessMask == 0x20000)
                                {
                                    acl.RemoveAce(index);
                                    break;
                                }
                            WriteAcl(acl);
                        }
                        OwnerLeases.Remove(key);
                    }
                    else
                        state.References--;
                    _ownerRightsHeld = false;
                }
                if (_writeHeld)
                {
                    WritableObjects.Remove(key);
                    _writeHeld = false;
                }
                _handle.Dispose();
            }
        }
    }
}
