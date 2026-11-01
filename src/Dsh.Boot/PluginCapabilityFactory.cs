using System.Buffers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Boot;

/** Implements the same file, HTTP and tool capabilities for local and isolated plugin SDKs. */
public sealed class PluginCapabilityFactory(Context context, HarnessHome home, HarnessSettings settings)
    : IPluginHostFactory, IDisposable
{
    internal const int MaximumChunkBytes = 256 * 1024;
    internal const int MaximumRegistrationBytes = 8 * 1024 * 1024;
    internal const int MaximumRegistrations = 256;
    internal const int MaximumLogCharacters = 4096;
    private const int MaximumEncodedChunkCharacters = ((MaximumChunkBytes + 2) / 3) * 4;
    private const int CopyBufferBytes = 16 * 1024;
    private static readonly JsonElement EmptyConfiguration = CreateEmptyConfiguration();
    private readonly HttpClient _http = new(context.GetProp(OutboundProxy.PropName) is OutboundProxy proxy
        ? proxy.CreateHandler(useCookies: false)
        : new HttpClientHandler { UseCookies = false, UseProxy = false });

    public IPluginHost Create(string package, Context pluginContext) => new LocalHost(this, package, pluginContext);

    public async Task<IDisposable?> ActivateAsync(string package, IDshIsolatedPlugin plugin, Context pluginContext,
        object? configuration, CancellationToken cancellationToken = default)
    {
        var host = new LocalHost(this, package, pluginContext);
        try
        {
            var json = PluginConfigurationJson.Write(configuration);
            using var document = json.Length == 0 ? null : JsonDocument.Parse(json);
            var value = document?.RootElement.Clone() ?? EmptyConfiguration;
            var lifetime = await plugin.InitializeAsync(host, value, cancellationToken).ConfigureAwait(false);
            return new LocalLifetime(host, lifetime);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static JsonElement CreateEmptyConfiguration()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    internal ValueTask<JsonElement> ExecuteAsync(string package, string operation, JsonElement arguments,
        ToolRunContext? execution, PluginPermissions? ceiling, CancellationToken cancellationToken)
        => operation switch
        {
            PluginHostOperations.ReadFile => ReadFileAsync(package, arguments, execution, ceiling, cancellationToken),
            PluginHostOperations.WriteFile => WriteFileAsync(package, arguments, execution, ceiling, cancellationToken),
            PluginHostOperations.SendHttp => SendHttpAsync(package, arguments, ceiling, cancellationToken),
            _ => throw new RuntimeException("PLUGIN_HOST_OPERATION_UNKNOWN", "The plugin requested an unsupported host operation."),
        };

    private readonly record struct AccessGrant(bool Isolated, bool AllowNetwork);

    private AccessGrant Require(string package, PluginPermission permission, PluginPermissions? ceiling)
    {
        var active = File.Exists(home.SettingsFile) ? HarnessSettings.LoadForReview(home) : settings;
        var configured = active.Plugins.GetValueOrDefault(package)?.Permissions ?? PluginPermissions.AllAllowed;
        if (!configured.Allows(permission) || ceiling is not null && !ceiling.Allows(permission))
            throw new RuntimeException("PLUGIN_PERMISSION_DENIED", $"The plugin's {PluginPermissions.KeyOf(permission)} permission is denied.");
        return new AccessGrant(active.Sandbox?.Enabled == true, configured.Network && ceiling?.Network != false);
    }

    private string ResolvePath(JsonElement arguments, ToolRunContext? execution, bool allowNetwork)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("The file operation requires a path.");
        SandboxFileAccess.DemandFilePath(path);
        var cwd = execution?.Agent?.Session.WorkingDirectory
            ?? (context.GetProp("harnessOptions") as HarnessOptions)?.Cwd
            ?? Environment.CurrentDirectory;
        return Dsh.Transport.PinnedFileAccess.Canonicalize(Path.GetFullPath(path, cwd), allowNetwork);
    }

    private async ValueTask<JsonElement> ReadFileAsync(string package, JsonElement arguments,
        ToolRunContext? execution, PluginPermissions? ceiling, CancellationToken cancellationToken)
    {
        var grant = Require(package, PluginPermission.Read, ceiling);
        var path = ResolvePath(arguments, execution, grant.AllowNetwork);
        SandboxFileAccess.DemandOrdinaryFile(path, allowMissing: false);
        var offset = arguments.TryGetProperty("offset", out var position) ? position.GetInt64() : 0;
        var count = arguments.TryGetProperty("count", out var requested) ? requested.GetInt32() : MaximumChunkBytes;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumChunkBytes);
        await using var file = PinnedFileAccess.Open(path, PinnedFileMode.Read, target =>
        {
            if (!target.IsDirectory && target.Identity is not null)
                Require(package, PluginPermission.Read, ceiling);
        }, grant.AllowNetwork);
        var stream = file.Stream;
        stream.Position = offset;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            var result = new PluginFileReadResult(Convert.ToBase64String(buffer, 0, read), stream.Length,
                read == 0 || offset + read >= stream.Length);
            return JsonSerializer.SerializeToElement(result, PluginBrokerJsonContext.Default.PluginFileReadResult);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async ValueTask<JsonElement> WriteFileAsync(string package, JsonElement arguments,
        ToolRunContext? execution, PluginPermissions? ceiling, CancellationToken cancellationToken)
    {
        var grant = Require(package, PluginPermission.Write, ceiling);
        var path = ResolvePath(arguments, execution, grant.AllowNetwork);
        SandboxFileAccess.DemandOrdinaryFile(path, allowMissing: true);
        var gate = context.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false);
        var settingsFile = gate?.Owns(path) == true || SettingsFilePath.SameFile(path, home.SettingsFile);
        var data = DecodeBody(arguments, "data");
        var append = arguments.TryGetProperty("append", out var appending) && appending.GetBoolean();
        if (settingsFile)
            await WriteSettingsAsync(package, path, data, append, execution, ceiling, gate, cancellationToken).ConfigureAwait(false);
        else
        {
            await WriteBytesAsync(path, data, append, target =>
            {
                if (!target.IsDirectory)
                    Require(package, PluginPermission.Write, ceiling);
                if (gate?.Owns(target) == true)
                    throw new RuntimeException("SETTINGS_TARGET_CHANGED", "The file target changed to active settings; propose the settings diff explicitly.");
                if (grant.Isolated || ceiling is not null)
                    (context.Get<PluginHostAuthority>(PluginHostAuthority.ServiceName, false)
                        ?? throw new RuntimeException("PLUGIN_HOST_INTEGRITY_UNAVAILABLE", "The host code-integrity boundary is unavailable.")).DemandWritable(target);
            }, grant.AllowNetwork, cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.SerializeToElement(new PluginFileWriteResult(data.Length),
            PluginBrokerJsonContext.Default.PluginFileWriteResult);
    }

    private async Task WriteSettingsAsync(string package, string path, byte[] data, bool append, ToolRunContext? execution,
        PluginPermissions? ceiling, ISettingsMutationGate? gate, CancellationToken cancellationToken)
    {
        if (gate is null || execution is null)
            throw new RuntimeException("SETTINGS_APPROVAL_UNAVAILABLE", "Settings changes require an active tool call and the host settings approval boundary.");
        var target = gate.SettingsPath;
        var before = File.Exists(target)
            ? await SettingsMutationLimits.ReadTextAsync(target, cancellationToken).ConfigureAwait(false) : "";
        var text = Encoding.UTF8.GetString(data);
        var after = append ? $"{before}{text}" : text;
        if (!SettingsMutationLimits.CanReview(after))
            throw new RuntimeException("SETTINGS_CONTENT_TOO_LARGE", "The settings change exceeds the broker's bounded review capacity.");
        await gate.CommitAsync(new SettingsMutationRequest(path, before, after, execution), signal =>
        {
            var grant = Require(package, PluginPermission.Write, ceiling);
            return WriteBytesAsync(target, Encoding.UTF8.GetBytes(after), append: false, actual =>
            {
                if (!actual.IsDirectory && !gate.Owns(actual))
                    throw new RuntimeException("SETTINGS_TARGET_CHANGED", "The settings target changed before publication.");
            }, grant.AllowNetwork, signal);
        }, cancellationToken).ConfigureAwait(false);
        execution.RecordFileChange(target);
    }

    private static async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> data, bool append,
        Action<PinnedFileTarget> authorize, bool allowNetwork, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var file = PinnedFileAccess.Open(path, append ? PinnedFileMode.Append : PinnedFileMode.Replace, authorize, allowNetwork);
        await file.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await file.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> SendHttpAsync(string package, JsonElement arguments,
        PluginPermissions? ceiling, CancellationToken cancellationToken)
    {
        Require(package, PluginPermission.Network, ceiling);
        var url = arguments.GetProperty("url").GetString() ?? throw new ArgumentException("The HTTP operation requires a URL.");
        var method = arguments.TryGetProperty("method", out var requested) ? requested.GetString() : null;
        using var request = new HttpRequestMessage(new HttpMethod(method ?? "GET"), url);
        if (arguments.TryGetProperty("body", out _))
            request.Content = new ByteArrayContent(DecodeBody(arguments, "body"));
        AddHeaders(request, arguments);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await ReadResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            headers[header.Key] = header.Value.ToArray();
        return JsonSerializer.SerializeToElement(new PluginHttpResult((int)response.StatusCode, headers, body),
            PluginBrokerJsonContext.Default.PluginHttpResult);
    }

    private static void AddHeaders(HttpRequestMessage request, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("headers", out var headers))
            return;
        foreach (var header in headers.EnumerateObject())
        {
            var values = header.Value.ValueKind == JsonValueKind.Array
                ? header.Value.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                : [header.Value.GetString() ?? ""];
            if (!request.Headers.TryAddWithoutValidation(header.Name, values))
            {
                request.Content ??= new ByteArrayContent([]);
                if (!request.Content.Headers.TryAddWithoutValidation(header.Name, values))
                    throw new ArgumentException("The HTTP request contains an unsupported header.");
            }
        }
    }

    private static async Task<string> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumChunkBytes)
            throw new RuntimeException("PLUGIN_HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the bounded broker response size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > MaximumChunkBytes)
                    throw new RuntimeException("PLUGIN_HTTP_RESPONSE_TOO_LARGE", "The HTTP response exceeds the bounded broker response size.");
                output.Write(buffer, 0, read);
            }
            return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static byte[] DecodeBody(JsonElement arguments, string key)
    {
        var encoded = arguments.GetProperty(key).GetString() ?? throw new ArgumentException("File and HTTP bodies must be base64 strings.");
        if (encoded.Length > MaximumEncodedChunkCharacters)
            throw new RuntimeException("PLUGIN_REQUEST_TOO_LARGE", "The request exceeds the bounded broker chunk size.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaximumChunkBytes)
            throw new RuntimeException("PLUGIN_REQUEST_TOO_LARGE", "The request exceeds the bounded broker chunk size.");
        return bytes;
    }

    public void Dispose() => _http.Dispose();

    private sealed class LocalHost(PluginCapabilityFactory factory, string package, Context pluginContext)
        : IPluginHost, IDisposable
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<int, ToolLease> _registrations = [];
        private int _nextHandle;
        private int _registrationBytes;
        private bool _disposed;

        public IDisposable RegisterTool(PluginToolDefinition definition)
        {
            var schema = definition.Parameters.GetRawText();
            var bytes = Encoding.UTF8.GetByteCount(schema) + Encoding.UTF8.GetByteCount(definition.Name)
                + Encoding.UTF8.GetByteCount(definition.Description);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_registrations.Count >= MaximumRegistrations || bytes > MaximumRegistrationBytes - _registrationBytes)
                    throw new RuntimeException("PLUGIN_REGISTRATION_LIMIT", "The plugin exceeded its bounded tool registration capacity.");
                var tools = pluginContext.Get<ToolRuntime>(ToolRuntime.ServiceName)
                    ?? throw new InvalidOperationException("The tools service is required to register plugin tools.");
                var registration = tools.RegisterForPlugin(package, CreateDefinition(definition, schema));
                var handle = checked(++_nextHandle);
                var lease = new ToolLease(this, handle, bytes, registration);
                _registrations.Add(handle, lease);
                _registrationBytes += bytes;
                return lease;
            }
        }

        private static ToolDefinition CreateDefinition(PluginToolDefinition definition, string schema) => new()
        {
            Name = definition.Name,
            Description = definition.Description,
            Parameters = JsonNode.Parse(schema)?.AsObject() ?? throw new ArgumentException("Plugin parameters must be an object schema."),
            Output = new ToolOutputDefinition([], (_, value) => [new TextBlock(value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : value.GetRawText())]),
            Execute = async (arguments, execution) => await definition.Execute(arguments, execution.Signal).ConfigureAwait(false),
        };

        public ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return factory.ExecuteAsync(package, operation, arguments, SettingsMutationScope.Current?.Context,
                ceiling: null, cancellationToken);
        }

        public void Log(int level, string message)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var logger = pluginContext.LoggerFor(package);
            var bounded = message.Length <= MaximumLogCharacters ? message : message[..MaximumLogCharacters];
            switch (level)
            {
                case 0: logger.Debug("%s", bounded); break;
                case 2: logger.Warn("%s", bounded); break;
                case 3: logger.Error("%s", bounded); break;
                default: logger.Info("%s", bounded); break;
            }
        }

        private void Release(int handle, int bytes)
        {
            lock (_sync)
            {
                if (_registrations.Remove(handle))
                    _registrationBytes -= bytes;
            }
        }

        public void Dispose()
        {
            ToolLease[] leases;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                leases = _registrations.Values.ToArray();
                _registrations.Clear();
                _registrationBytes = 0;
            }
            foreach (var lease in leases)
                lease.Dispose();
        }

        private sealed class ToolLease(LocalHost owner, int handle, int bytes, IDisposable registration) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                owner.Release(handle, bytes);
                registration.Dispose();
            }
        }
    }

    private sealed class LocalLifetime(LocalHost host, IAsyncDisposable? lifetime) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                lifetime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}

internal sealed record PluginFileReadResult(string Data, long Length, bool Eof);
internal sealed record PluginFileWriteResult(int BytesWritten);
internal sealed record PluginHttpResult(int Status, Dictionary<string, string[]> Headers, string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginFileReadResult))]
[JsonSerializable(typeof(PluginFileWriteResult))]
[JsonSerializable(typeof(PluginHttpResult))]
internal sealed partial class PluginBrokerJsonContext : JsonSerializerContext;
