using System.ComponentModel;
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
    private const int ProtectedWrite = 0xd0156;
    private readonly List<Entry> _entries = [];
    private readonly SecurityIdentifier _sid;
    private bool _disposed;

    private WindowsAclLease(SecurityIdentifier sid) => _sid = sid;

    public static WindowsAclLease Create(nint sid, SandboxPolicy policy)
    {
        var lease = new WindowsAclLease(new SecurityIdentifier(sid));
        try
        {
            var paths = new Dictionary<string, (bool Write, bool Directory)>(StringComparer.OrdinalIgnoreCase);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var grant in policy.Writable.Concat(policy.Readable))
            {
                if (SandboxPolicy.Contains(windows, grant.Source))
                    continue;
                AddTree(paths, grant.Source, grant.IsDirectory, policy.Writable.Contains(grant));
            }
            foreach (var denied in policy.Denied)
            {
                if (File.Exists(denied) || Directory.Exists(denied))
                    AddTree(paths, denied, Directory.Exists(denied), false);
                else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                    throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
            }
            foreach (var (path, access) in paths)
            {
                var denyWrite = policy.Denied.Any(denied => SandboxPolicy.Contains(denied, path))
                    || policy.Readable.Any(read => SandboxPolicy.Contains(read.Source, path));
                var entry = new Entry(path);
                lease._entries.Add(entry);
                entry.Grant(lease._sid, access.Write && !denyWrite, access.Directory, denyWrite);
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

    private static void AddTree(Dictionary<string, (bool Write, bool Directory)> paths, string root, bool directory, bool write)
    {
        paths[root] = (write, directory);
        if (!directory)
            return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            paths[path] = (write, Directory.Exists(path));
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

    private sealed class Entry(string path)
    {
        private byte[]? _originalLabel;
        private bool _labelChanged;
        private bool _sidGranted;

        private SafeFileHandle Open()
        {
            var handle = WindowsNative.CreateFile(path, 0xe0000, 7, 0, 3, 0x02000000, 0);
            if (!handle.IsInvalid)
                return handle;
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Cannot open an AppContainer filesystem grant.");
        }

        public void Grant(SecurityIdentifier sid, bool write, bool directory, bool denyWrite)
        {
            using var handle = Open();
            var acl = ReadAcl(handle);
            var inherited = directory ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
            if (denyWrite)
                acl.InsertAce(0, new CommonAce(inherited, AceQualifier.AccessDenied, ProtectedWrite, sid, false, null));
            acl.InsertAce(acl.Count, new CommonAce(inherited, AceQualifier.AccessAllowed, write ? Modify : ReadExecute, sid, false, null));
            _sidGranted = true;
            WriteAcl(handle, acl);
            if (!write)
                return;
            _originalLabel = ReadAclBytes(handle, 0x10);
            if (!WindowsNative.ConvertStringSecurityDescriptorToSecurityDescriptor(directory ? "S:(ML;OICI;NW;;;LW)" : "S:(ML;;NW;;;LW)", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot construct the AppContainer writable label.");
            try
            {
                if (!WindowsNative.GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the AppContainer writable label.");
                Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, sacl));
                _labelChanged = true;
            }
            finally { WindowsNative.LocalFree(descriptor); }
        }

        public unsafe void Remove(SecurityIdentifier sid)
        {
            if (!_sidGranted || !File.Exists(path) && !Directory.Exists(path))
                return;
            using var handle = Open();
            var acl = ReadAcl(handle);
            for (var index = acl.Count - 1; index >= 0; index--)
                if (acl[index] is KnownAce ace && ace.SecurityIdentifier == sid)
                    acl.RemoveAce(index);
            WriteAcl(handle, acl);
            if (_labelChanged)
                fixed (byte* label = _originalLabel)
                    Check(WindowsNative.SetSecurityInfo(handle, 1, 0x10, 0, 0, 0, (nint)label));
        }

        private static RawAcl ReadAcl(SafeFileHandle handle)
            => new(ReadAclBytes(handle, 4) ?? throw new NotSupportedException("An AppContainer filesystem grant requires an explicit ACL."), 0);

        private static byte[]? ReadAclBytes(SafeFileHandle handle, uint information)
        {
            Check(WindowsNative.GetSecurityInfo(handle, 1, information, out _, out _, out var dacl, out var sacl, out var descriptor));
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

        private static unsafe void WriteAcl(SafeFileHandle handle, RawAcl acl)
        {
            var bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            fixed (byte* pointer = bytes)
                Check(WindowsNative.SetSecurityInfo(handle, 1, 4, 0, 0, (nint)pointer, 0));
        }

        private static void Check(uint error)
        {
            if (error != 0)
                throw new Win32Exception((int)error, "Cannot apply or remove the AppContainer filesystem grant.");
        }
    }
}
