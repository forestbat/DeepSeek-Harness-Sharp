using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSandbox : ISandboxLifetime
{
    private const uint DetachedProcess = 0x00000008;
    private readonly SafeFileHandle _process, _job;
    private readonly WindowsAclLease _acl;
    private readonly string _profile;
    private readonly Lock _gate = new();
    private bool _nativeHandlesClosed;
    private Exception? _terminationFailure;
    public int Id { get; }
    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    public Task<int> Exit { get; }

    private WindowsSandbox(int id, SafeFileHandle process, SafeFileHandle job,
        WindowsAclLease acl, string profile, Stream input, Stream output, Stream error)
    {
        Id = id;
        _process = process;
        _job = job;
        _acl = acl;
        _profile = profile;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
        Exit = ObserveAsync();
    }

    public static WindowsSandbox Start(SandboxPolicy policy)
    {
        var profile = $"Dsh.Sandbox.{Guid.NewGuid():N}";
        var result = WindowsNative.CreateAppContainerProfile(profile, "Dsh sandbox", "Ephemeral isolated process", 0, 0, out var sid);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        WindowsAclLease? acl = null;
        SafeFileHandle? job = null, process = null;
        WindowsPipe? input = null, output = null, error = null;
        Stream? inputStream = null, outputStream = null, errorStream = null;
        try
        {
            acl = WindowsAclLease.Create(sid, policy);
            job = WindowsNative.CreateJobObject(0, null);
            CheckHandle(job, "create the sandbox job");
            ConfigureJob(job, policy);
            input = WindowsPipe.Create(parentWrites: true);
            output = WindowsPipe.Create(parentWrites: false);
            error = WindowsPipe.Create(parentWrites: false);
            using var attributes = new ProcessAttributes(sid, policy.AllowNetwork, policy.AllowChildProcesses, [input.Child, output.Child, error.Child]);
            var startup = new WindowsNative.StartupInfoEx
            {
                Startup = new WindowsNative.StartupInfo
                {
                    Size = Marshal.SizeOf<WindowsNative.StartupInfoEx>(), Flags = 0x100,
                    Input = input.Child.DangerousGetHandle(), Output = output.Child.DangerousGetHandle(), Error = error.Child.DangerousGetHandle()
                },
                Attributes = attributes.List
            };
            var command = new StringBuilder(string.Join(' ', new[] { policy.Executable }.Concat(policy.Arguments).Select(QuoteArgument)));
            if (command.Length >= 32767)
                throw new ArgumentException("The Windows sandbox command line exceeds the native 32766-character bound.");
            var environment = Marshal.StringToHGlobalUni($"{string.Join('\0', policy.EnvironmentEntries)}\0\0");
            WindowsNative.ProcessInformation information;
            try
            {
                // Keep the canonical DOS working directory: cmd treats a forced extended-length prefix as UNC and changes directories.
                Check(WindowsNative.CreateProcess(WindowsNative.PathForNative(policy.Executable), command, 0, 0, true,
                    0x00000004 | 0x00000400 | 0x00080000 | DetachedProcess, environment, policy.WorkingDirectory, ref startup, out information), "create the suspended AppContainer process");
            }
            finally { Marshal.FreeHGlobal(environment); }
            process = new SafeFileHandle(information.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
            try
            {
                Check(WindowsNative.AssignProcessToJobObject(job, process), "assign the suspended process to its non-breakaway job");
                input.Child.Dispose();
                output.Child.Dispose();
                error.Child.Dispose();
                inputStream = input.TakeParent(FileAccess.Write);
                outputStream = output.TakeParent(FileAccess.Read);
                errorStream = error.TakeParent(FileAccess.Read);
                if (WindowsNative.ResumeThread(thread) == uint.MaxValue)
                    throw NativeError("resume the AppContainer process");
                return new WindowsSandbox((int)information.ProcessId, process, job, acl, profile,
                    inputStream, outputStream, errorStream);
            }
            catch
            {
                WindowsNative.TerminateProcess(process, 125);
                WindowsNative.TerminateJobObject(job, 125);
                WindowsNative.WaitForSingleObject(process, uint.MaxValue);
                while (ActiveProcesses(job) != 0)
                    Thread.Sleep(25);
                throw;
            }
        }
        catch (Exception launchFailure)
        {
            List<Exception>? failures = null;
            DisposeResource(inputStream, ref failures);
            DisposeResource(outputStream, ref failures);
            DisposeResource(errorStream, ref failures);
            DisposeResource(process, ref failures);
            DisposeResource(job, ref failures);
            DisposeResource(acl, ref failures);
            DeleteProfile(profile, ref failures);
            if (failures is not null)
            {
                failures.Insert(0, launchFailure);
                throw new AggregateException("The Windows sandbox launch and its cleanup failed.", failures);
            }
            throw;
        }
        finally
        {
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            WindowsNative.FreeSid(sid);
        }
    }

    private static unsafe void ConfigureJob(SafeFileHandle job, SandboxPolicy policy)
    {
        var limit = new WindowsNative.JobExtendedLimit
        {
            Basic = new WindowsNative.JobBasicLimit { Flags = 0x2000 | 0x8 | 0x400, ActiveProcessLimit = policy.AllowChildProcesses ? 256u : 1u },
        };
        Check(WindowsNative.SetInformationJobObject(job, 9, (nint)(&limit), (uint)sizeof(WindowsNative.JobExtendedLimit)), "set the job process limit and termination policy");
    }

    private async Task<int> ObserveAsync()
    {
        try
        {
            while (!RootExited())
            {
                if (_terminationFailure is not null)
                    throw new IOException("Terminating the exact Windows sandbox job failed.", _terminationFailure);
                await Task.Delay(25).ConfigureAwait(false);
            }
            Check(WindowsNative.GetExitCodeProcess(_process, out var code), "read the sandbox process exit code");
            Kill();
            await WaitForEmptyJobAsync().ConfigureAwait(false);
            return unchecked((int)code);
        }
        catch
        {
            Kill();
            await WaitForEmptyJobAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool RootExited()
    {
        var result = WindowsNative.WaitForSingleObject(_process, 0);
        if (result != 0 && result != 0x102)
            throw NativeError("wait for the owned AppContainer process");
        return result == 0;
    }

    private async Task WaitForEmptyJobAsync()
    {
        while (ActiveProcesses(_job) != 0)
        {
            if (_terminationFailure is not null)
                throw new IOException("Terminating the exact Windows sandbox job failed.", _terminationFailure);
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        Check(WindowsNative.QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<WindowsNative.JobAccounting>(), 0), "query the owned sandbox job");
        return accounting.ActiveProcesses;
    }


    public void Kill()
    {
        lock (_gate)
        {
            if (!_nativeHandlesClosed && !WindowsNative.TerminateJobObject(_job, 125))
                _terminationFailure = NativeError("terminate the sandbox job");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        List<Exception>? failures = null;
        try { await Exit.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        List<Exception>? cleanupFailures = null;
        try { await StandardInput.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await StandardOutput.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await StandardError.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        lock (_gate)
        {
            _nativeHandlesClosed = true;
            DisposeResource(_process, ref failures);
            DisposeResource(_job, ref failures);
        }
        DisposeResource(_acl, ref cleanupFailures);
        DeleteProfile(_profile, ref cleanupFailures);
        if (cleanupFailures is not null)
            (failures ??= []).AddRange(cleanupFailures);
        if (failures is not null)
            throw new AggregateException("Disposing the Windows sandbox failed.", failures);
    }

    private static void DisposeResource(IDisposable? resource, ref List<Exception>? failures)
    {
        try { resource?.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }

    private static void DeleteProfile(string profile, ref List<Exception>? failures)
    {
        try
        {
            var result = WindowsNative.DeleteAppContainerProfile(profile);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && !value.Any(static character => char.IsWhiteSpace(character) || character == '"'))
            return value;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static Win32Exception NativeError(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        var status = WindowsNative.RtlGetLastNtStatus();
        return new Win32Exception(error, $"Cannot {operation} (Win32 error {error}, last NTSTATUS 0x{status:x8}).");
    }
    private static void Check(bool success, string operation) { if (!success) throw NativeError(operation); }
    private static void CheckHandle(SafeFileHandle handle, string operation) { if (handle.IsInvalid) throw NativeError(operation); }

    private sealed class WindowsPipe : IDisposable
    {
        public SafeFileHandle Child { get; }
        private SafeFileHandle? _parent;
        private WindowsPipe(SafeFileHandle parent, SafeFileHandle child) { _parent = parent; Child = child; }
        public static WindowsPipe Create(bool parentWrites)
        {
            var attributes = new WindowsNative.SecurityAttributes { Length = Marshal.SizeOf<WindowsNative.SecurityAttributes>(), Inherit = true };
            Check(WindowsNative.CreatePipe(out var read, out var write, ref attributes, 4096), "create sandbox stdio pipes");
            try
            {
                var parent = parentWrites ? write : read;
                Check(WindowsNative.SetHandleInformation(parent, 1, 0), "exclude the parent pipe handle from inheritance");
                return new WindowsPipe(parent, parentWrites ? read : write);
            }
            catch { read.Dispose(); write.Dispose(); throw; }
        }
        public Stream TakeParent(FileAccess access)
        {
            var handle = _parent!;
            _parent = null;
            try { return new FileStream(handle, access, 4096, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }
        public void Dispose() { Child.Dispose(); _parent?.Dispose(); }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        public nint List { get; private set; }
        private readonly List<nint> _allocations = [];
        private readonly List<nint> _sids = [];
        private bool _initialized;
        public unsafe ProcessAttributes(nint sid, bool network, bool children, SafeFileHandle[] handles)
        {
            try
            {
                nuint size = 0;
                WindowsNative.InitializeProcThreadAttributeList(0, 4, 0, ref size);
                List = Allocate(checked((int)size));
                Check(WindowsNative.InitializeProcThreadAttributeList(List, 4, 0, ref size), "initialize the explicit sandbox attribute list");
                _initialized = true;
                var capabilities = new WindowsNative.SecurityCapabilities { Sid = sid };
                if (network)
                {
                    var values = WindowsNative.NetworkCapabilities;
                    capabilities.Capabilities = Allocate(values.Length * sizeof(WindowsNative.SidAndAttributes));
                    capabilities.CapabilityCount = (uint)values.Length;
                    for (var index = 0; index < values.Length; index++)
                    {
                        Check(WindowsNative.ConvertStringSidToSid(values[index], out var capability), "construct a network capability SID");
                        _sids.Add(capability);
                        ((WindowsNative.SidAndAttributes*)capabilities.Capabilities)[index] = new WindowsNative.SidAndAttributes { Sid = capability, Attributes = 4 };
                    }
                }
                Set(0x20009, &capabilities, sizeof(WindowsNative.SecurityCapabilities));
                var list = Allocate(handles.Length * nint.Size);
                for (var index = 0; index < handles.Length; index++)
                    Marshal.WriteIntPtr(list, index * nint.Size, handles[index].DangerousGetHandle());
                Check(WindowsNative.UpdateProcThreadAttribute(List, 0, 0x20002, list, (nuint)(handles.Length * nint.Size), 0, 0), "restrict inherited handles to sandbox stdio");
                uint lpac = 1;
                Set(0x2000f, &lpac, sizeof(uint));
                uint childPolicy = children ? 0u : 1u;
                Set(0x2000e, &childPolicy, sizeof(uint));
            }
            catch { Dispose(); throw; }
        }
        private unsafe void Set(nuint attribute, void* value, int bytes)
        {
            var pointer = Allocate(bytes);
            Buffer.MemoryCopy(value, (void*)pointer, bytes, bytes);
            Check(WindowsNative.UpdateProcThreadAttribute(List, 0, attribute, pointer, (nuint)bytes, 0, 0), "configure AppContainer security attributes");
        }
        private nint Allocate(int bytes) { var result = Marshal.AllocHGlobal(bytes); _allocations.Add(result); return result; }
        public void Dispose()
        {
            if (_initialized) WindowsNative.DeleteProcThreadAttributeList(List);
            _initialized = false;
            foreach (var pointer in _allocations) Marshal.FreeHGlobal(pointer);
            foreach (var sid in _sids) WindowsNative.LocalFree(sid);
            _allocations.Clear();
            _sids.Clear();
            List = 0;
        }
    }
}
