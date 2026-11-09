using System.Buffers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Plugins.Sandbox;

/** Implements the same file, HTTP and tool capabilities for local and isolated plugin SDKs. */
public sealed class PluginCapabilityFactory(Context context, PluginExecutionEnvironment environment)
    : IPluginHostFactory, IDisposable
{
    internal const int MaximumChunkBytes = 256 * 1024;
    private const int CopyBufferBytes = 16 * 1024;

    private readonly HttpClient _http = new(context.GetProp(OutboundProxy.PropName) is OutboundProxy proxy
        ? proxy.CreateHandler(useCookies: false)
        : new HttpClientHandler { UseCookies = false, UseProxy = false });

    private SafetyBoundary Safety => SafetyBoundary.Resolve(context)
        ?? throw new InvalidOperationException("Plugin operations require the host safety boundary.");
    public PluginCapabilityHost Create(string package, Context pluginContext)
    {
        var host = new PluginCapabilityHost(ExecuteAsync, package, pluginContext);
        pluginContext.Effect(() => host, IPluginHostFactory.ServiceName);
        return host;
    }

    IPluginHost IPluginHostFactory.Create(string package, Context pluginContext) => Create(package, pluginContext);


    internal ValueTask<JsonElement> ExecuteAsync(string package, string operation, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
        => operation switch
        {
            PluginHostOperations.ReadFile => ReadFileAsync(package, arguments, execution, cancellationToken),
            PluginHostOperations.WriteFile => WriteFileAsync(package, arguments, execution, cancellationToken),
            PluginHostOperations.SendHttp => SendHttpAsync(package, arguments, execution, cancellationToken),
            _ => throw new RuntimeException("PLUGIN_HOST_OPERATION_UNKNOWN", "The plugin requested an unsupported host operation."),
        };

    private bool Require(string package, PluginPermission permission)
    {
        var active = environment.ReadSettings();
        var configured = active.Plugins.GetValueOrDefault(package)?.Permissions ?? PluginPermissions.AllAllowed;
        if (!configured.Allows(permission))
            throw new RuntimeException("PLUGIN_PERMISSION_DENIED", $"The plugin's {PluginPermissions.KeyOf(permission)} permission is denied.");
        return configured.Network;
    }

    private string ResolvePath(JsonElement arguments, ToolRunContext? execution, bool allowNetwork)
    {
        var path = arguments.GetProperty("path").GetString()
            ?? throw new ArgumentException("The file operation requires a path.");
        CanonicalFilePath.DemandFilePath(path);
        var cwd = execution?.Agent?.Session.WorkingDirectory
            ?? environment.WorkingDirectory;
        return CanonicalFilePath.Canonicalize(Path.GetFullPath(path, cwd), allowNetwork);
    }

    private async ValueTask<JsonElement> ReadFileAsync(string package, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
    {
        var allowNetwork = Require(package, PluginPermission.Read);
        var path = ResolvePath(arguments, execution, allowNetwork);
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: false);
        var offset = arguments.TryGetProperty("offset", out var position) ? position.GetInt64() : 0;
        var count = arguments.TryGetProperty("count", out var requested) ? requested.GetInt32() : MaximumChunkBytes;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumChunkBytes);
        await Safety.AuthorizeFileAsync(execution?.PermissionNamespace ?? SafetyBoundary.SafetyNamespace, execution, path, "read", cancellationToken, package).ConfigureAwait(false);
        Require(package, PluginPermission.Read);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            CopyBufferBytes, FileOptions.Asynchronous);
        if (OperatingSystem.IsLinux())
            LinuxMountTable.DemandOrdinaryFile(stream.SafeFileHandle);
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
        ToolRunContext? execution, CancellationToken cancellationToken)
    {
        var allowNetwork = Require(package, PluginPermission.Write);
        var path = ResolvePath(arguments, execution, allowNetwork);
        FilePathAccess.DemandOrdinaryFile(path, allowMissing: true);
        var gate = context.Get<Func<SettingsMutationRequest, Func<string, CancellationToken, Task>, CancellationToken, Task>>(SettingsMutation.ServiceName, false);
        var activeSettings = (context.GetProp("harnessOptions") as HarnessOptions)?.Home.SettingsFile ?? environment.SettingsPath;
        var data = DecodeBody(arguments, "data");
        var append = arguments.TryGetProperty("append", out var appending) && appending.GetBoolean();
        if (CanonicalFilePath.SameFile(path, activeSettings) || CanonicalFilePath.SameFile(path, environment.SettingsPath))
            await WriteSettingsAsync(package, path, data, append, execution, gate, cancellationToken).ConfigureAwait(false);
        else
        {
            await Safety.AuthorizeFileAsync(execution?.PermissionNamespace ?? SafetyBoundary.SafetyNamespace, execution, path, "write", cancellationToken, package).ConfigureAwait(false);
            Require(package, PluginPermission.Write);
            await WriteBytesAsync(path, data, append, cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.SerializeToElement(new PluginFileWriteResult(data.Length),
            PluginBrokerJsonContext.Default.PluginFileWriteResult);
    }

    private async Task WriteSettingsAsync(string package, string path, byte[] data, bool append, ToolRunContext? execution,
        Func<SettingsMutationRequest, Func<string, CancellationToken, Task>, CancellationToken, Task>? gate, CancellationToken cancellationToken)
    {
        if (gate is null || execution is null)
            throw new RuntimeException("SETTINGS_APPROVAL_UNAVAILABLE", "Settings changes require an active tool call and the host settings approval service.");
        var target = CanonicalFilePath.Canonicalize((context.GetProp("harnessOptions") as HarnessOptions)?.Home.SettingsFile ?? environment.SettingsPath);
        await Safety.AuthorizeFileAsync(SafetyBoundary.SafetyNamespace, execution, target, "read", cancellationToken, package,
            settings: new SettingsApproval()).ConfigureAwait(false);
        var before = File.Exists(target) ? await SettingsMutation.ReadTextAsync(target, cancellationToken).ConfigureAwait(false) : "";
        var text = Encoding.UTF8.GetString(data);
        var after = append ? $"{before}{text}" : text;
        await gate(new SettingsMutationRequest(path, before, after, execution), (temporary, signal) =>
        {
            Require(package, PluginPermission.Write);
            return WriteBytesAsync(temporary, Encoding.UTF8.GetBytes(after), append: false, signal);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> data, bool append, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = append ? FileMode.Append : OperatingSystem.IsLinux() ? FileMode.OpenOrCreate : FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        if (OperatingSystem.IsLinux())
        {
            LinuxMountTable.DemandOrdinaryFile(stream.SafeFileHandle);
            if (!append)
                stream.SetLength(0);
        }
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> SendHttpAsync(string package, JsonElement arguments,
        ToolRunContext? execution, CancellationToken cancellationToken)
    {
        Require(package, PluginPermission.Network);
        await Safety.AuthorizeOperationAsync(execution?.PermissionNamespace ?? SafetyBoundary.SafetyNamespace, execution, cancellationToken, package).ConfigureAwait(false);
        var url = arguments.GetProperty("url").GetString() ?? throw new ArgumentException("The HTTP operation requires a URL.");
        var method = arguments.TryGetProperty("method", out var requested) ? requested.GetString() : null;
        using var request = new HttpRequestMessage(new HttpMethod(method ?? "GET"), url);
        if (arguments.TryGetProperty("body", out _))
            request.Content = new ByteArrayContent(DecodeBody(arguments, "body"));
        AddHeaders(request, arguments);
        Require(package, PluginPermission.Network);
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
        return Convert.FromBase64String(encoded);
    }

    public void Dispose() => _http.Dispose();


}

internal sealed record PluginFileReadResult(string Data, long Length, bool Eof);
internal sealed record PluginFileWriteResult(int BytesWritten);
internal sealed record PluginHttpResult(int Status, Dictionary<string, string[]> Headers, string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginFileReadResult))]
[JsonSerializable(typeof(PluginFileWriteResult))]
[JsonSerializable(typeof(PluginHttpResult))]
internal sealed partial class PluginBrokerJsonContext : JsonSerializerContext;
