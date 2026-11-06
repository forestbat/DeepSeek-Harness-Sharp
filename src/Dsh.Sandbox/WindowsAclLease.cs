using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Grants a unique AppContainer SID access to declared roots and removes those grants on disposal.</summary>
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(denied) || Directory.Exists(denied))
                Grant(denied, ReadExecute, WriteAccess, Directory.Exists(denied), false, cancellationToken);
            else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
            ProtectAncestors(denied);
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
        private WritableLabel? _label;
        private bool _sidGranted;

        private SafeFileHandle Open(bool writable)
        {
            var handle = WindowsNative.CreateFile(path, writable ? 0xe0000u : 0x60000u, 7, 0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
                return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open an AppContainer filesystem grant (Win32 error {error}).");
        }

        public void Grant(SecurityIdentifier sid, int access, int deniedAccess, bool directory, bool writable,
            CancellationToken cancellationToken)
        {
            // Serialize native read/modify/write so concurrent leases retain each other's SID grants.
            lock (AclGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = Open(writable);
                var acl = ReadAcl(handle);
                var inherited = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
                if (deniedAccess != 0)
                    acl.InsertAce(0, new CommonAce(inherited, AceQualifier.AccessDenied, deniedAccess, sid, false, null));
                if (access != 0)
                {
                    var insertion = 0;
                    while (insertion < acl.Count && (acl[insertion].AceFlags & AceFlags.Inherited) == 0)
                        insertion++;
                    acl.InsertAce(insertion, new CommonAce(inherited, AceQualifier.AccessAllowed, access, sid, false, null));
                }
                _sidGranted = true;
                // Windows propagates inheritable ACEs; protected descendants can still reject actual process access.
                WriteAcl(handle, acl);
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
                using var handle = Open(restoreLabel);
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
                    if (removedGrant)
                        WriteAcl(handle, acl);
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

        private static unsafe void WriteAcl(SafeFileHandle handle, RawAcl acl)
        {
            var bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            fixed (byte* pointer = bytes)
                Check(WindowsNative.SetSecurityInfo(handle, 1, 4, 0, 0, (nint)pointer, 0));
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
