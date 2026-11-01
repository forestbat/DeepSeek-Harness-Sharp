using Dsh.Llm;
using Dsh.Transport;
using System.Text;
using Dsh.Sandbox;

namespace Dsh.Core;

public readonly record struct SettingsMutationRequest(string Path, string Before, string After, ToolRunContext Context);

public interface ISettingsMutationGate
{
    const string ServiceName = "settingsMutations";
    string SettingsPath { get; }
    bool Owns(string path);
    bool Owns(PinnedFileTarget target)
    {
        if (Owns(target.Path))
            return true;
        if (target.IsDirectory || target.Identity is not { } actual || !File.Exists(SettingsPath))
            return false;
        var settings = NativeFileIdentity.Get(SettingsPath);
        return actual.Device == settings.Device && actual.Inode == settings.Inode;
    }
    Task CommitAsync(SettingsMutationRequest request, Func<CancellationToken, Task> commit, CancellationToken cancellationToken = default);
}

public static class SettingsMutationLimits
{
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    public static bool CanReview(string text)
        => text.Length <= MaxDocumentBytes && Encoding.UTF8.GetByteCount(text) <= MaxDocumentBytes;

    public static async Task<string> ReadTextAsync(string path, CancellationToken signal)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await ReadTextAsync(stream, signal).ConfigureAwait(false);
    }

    public static async Task<string> ReadTextAsync(Stream stream, CancellationToken signal)
    {
        var length = stream.Length;
        if (length > MaxDocumentBytes)
            throw new InvalidOperationException("The settings document exceeds the 4 MiB review limit.");
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, signal).ConfigureAwait(false);
        signal.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1)
            throw new IOException("Settings changed while their bounded snapshot was being read.");
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false));
        return reader.ReadToEnd();
    }
}

/** Trusted-host correlation only; this scope never grants authority to isolated code. */
public sealed class SettingsMutationScope : IDisposable
{
    private static readonly AsyncLocal<SettingsMutationScope?> Active = new();
    private readonly SettingsMutationScope? _previous;
    private bool _disposed;
    public static SettingsMutationScope? Current => Active.Value is { _disposed: false } scope ? scope : null;
    public ToolRunContext Context { get; }
    public ISettingsMutationGate? Gate { get; }
    private IRuntimeCodeIntegrity? CodeIntegrity { get; }
    public ISafetyBoundary? Safety { get; }

    public SettingsMutationScope(ToolRunContext context, ISettingsMutationGate? gate, IRuntimeCodeIntegrity? codeIntegrity = null,
        ISafetyBoundary? safety = null)
    {
        Context = context;
        Gate = gate;
        CodeIntegrity = codeIntegrity;
        Safety = safety;
        _previous = Active.Value;
        Active.Value = this;
    }

    public Task CommitAsync(string path, string before, string after, Func<CancellationToken, Task> commit)
        => (Gate ?? throw new InvalidOperationException("Settings mutations require the active settings approval service."))
            .CommitAsync(new(path, before, after, Context), commit, Context.Signal);

    public static async Task<string> ReadTextAsync(string path, ToolRunContext context, bool settingsReview = false)
    {
        var settings = Current?.Gate?.Owns(path) == true;
        await using var file = await SafetyFileAccess.OpenReadAsync(path, context,
            settingsReview: settings && (settingsReview || context.InitialFileIntent == "write")).ConfigureAwait(false);
        if (settings)
        {
            return await SettingsMutationLimits.ReadTextAsync(file.Stream, context.Signal).ConfigureAwait(false);
        }
        using var reader = new StreamReader(file.Stream, new UTF8Encoding(false, false));
        return await reader.ReadToEndAsync(context.Signal).ConfigureAwait(false);
    }

    public static async Task WriteTextAsync(string path, string before, string after, ToolRunContext context,
        PinnedFileMode mode = PinnedFileMode.Replace)
    {
        var gate = Current?.Gate;
        var authorizeSafety = await SafetyFileAccess.AuthorizeAsync(path, "write", context, context.Signal,
            settingsReview: gate?.Owns(path) == true).ConfigureAwait(false);
        if (gate is not null && gate.Owns(path))
        {
            var canonical = gate.SettingsPath;
            var authorizeCanonical = path == canonical ? authorizeSafety
                : await SafetyFileAccess.AuthorizeAsync(canonical, "write", context, context.Signal,
                    settingsReview: true).ConfigureAwait(false);
            await gate.CommitAsync(new(path, before, after, context), async signal =>
            {
                await WriteTextFileAsync(canonical, after, PinnedFileMode.Replace, target =>
                {
                    authorizeCanonical(target);
                    if (!target.IsDirectory && !gate.Owns(target))
                        throw new IOException("The settings target changed before publication.");
                }, signal).ConfigureAwait(false);
                context.RecordFileChange(canonical);
            }, context.Signal).ConfigureAwait(false);
            return;
        }
        await WriteTextFileAsync(path, after, mode, target =>
        {
            authorizeSafety(target);
            if (gate?.Owns(target) == true)
                throw new IOException("The file target changed to active settings; propose the settings diff explicitly.");
            if (context.ExternalProcessProtectionRequired)
                (Current?.CodeIntegrity ?? throw new InvalidOperationException("Protected writes require the runtime code integrity boundary."))
                    .DemandWritable(target);
        }, context.Signal).ConfigureAwait(false);
        context.RecordFileChange(path);
    }

    private static async Task WriteTextFileAsync(string path, string text, PinnedFileMode mode,
        Action<PinnedFileTarget> authorize, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        await using var file = PinnedFileAccess.Open(path, mode, authorize);
        await using (var writer = new StreamWriter(file.Stream, new UTF8Encoding(false, false), leaveOpen: true))
            await writer.WriteAsync(text.AsMemory(), signal).ConfigureAwait(false);
        await file.CompleteAsync(signal).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        Active.Value = _previous;
    }
}

public sealed record SettingsMutationAuditPayload(
    string Id, ToolCallId CallId, ToolCallId RootCallId, string ToolName,
    string Path, string Before, string After, string Outcome) : SessionEventPayload
{
    public const string EventType = "settings/mutation";
    public override string Type => EventType;
}
