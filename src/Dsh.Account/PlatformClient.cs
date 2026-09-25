using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Account;

public sealed record AuthInitRequest(string CodeChallenge, string State, string RedirectUri, string Locale, string LoginSource);

public sealed record AuthInitResult(string AuthorizeUrl, string AuthorizeId, int ExpiresIn);

public sealed record AuthExchangeResult(string Token, string AuthorizedUrl, JsonElement? User);

/**
 * 平台 HTTP 边界: 固定端点、响应信封、64 KiB 上限、禁止重定向。
 * 诊断只记接口路径/HTTP 状态/数值码/失败阶段, 绝不记请求头、正文或 token。
 */
public sealed class PlatformClient : IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly string _origin;
    private readonly string _clientPlatform;
    private readonly string _clientVersion;
    private readonly string _locale;
    private readonly int _timeoutMs;
    private readonly Action<string>? _log;

    public PlatformClient(HttpMessageHandler handler, string origin, string clientPlatform, string clientVersion, string locale,
        int timeoutMs, bool disposeHandler, Action<string>? log = null)
    {
        _http = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _origin = origin;
        _clientPlatform = clientPlatform;
        _clientVersion = clientVersion;
        _locale = locale;
        _timeoutMs = timeoutMs;
        _log = log;
    }

    public string Origin => _origin;

    public async Task<AuthInitResult> AuthInitAsync(AuthInitRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = request.State,
            ["redirect_uri"] = request.RedirectUri,
            ["locale"] = request.Locale,
            ["login_source"] = request.LoginSource,
        };
        var payload = await PostAsync("auth_init", body, null, cancellationToken);
        return new AuthInitResult(
            RequiredString(payload, "authorize_url"),
            RequiredString(payload, "authorize_id"),
            (int)(PlatformPayloads.OptionalInt(payload, "expires_in") ?? throw new AccountException(AccountFailure.Protocol)));
    }

    public async Task<AuthExchangeResult> AuthExchangeAsync(string code, string verifier, string redirectUri, string deviceId,
        string deviceModel, string osVersion, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["device_id"] = deviceId,
            ["device_model"] = deviceModel,
            ["os_version"] = osVersion,
        };
        var payload = await PostAsync("auth_exchange", body, null, cancellationToken);
        var token = RequiredString(payload, "token");
        if (token.Any(character => character is < '\x21' or > '\x7e'))
            throw new AccountException(AccountFailure.Protocol);
        var user = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("user", out var element) ? element.Clone() : (JsonElement?)null;
        return new AuthExchangeResult(token, RequiredString(payload, "authorized_url"), user);
    }

    public Task CancelAsync(string authorizeId, string codeVerifier, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["authorize_id"] = authorizeId, ["code_verifier"] = codeVerifier };
        return PostAsync("auth_cancel", body, null, cancellationToken);
    }

    public Task<JsonElement> GetCurrentUserAsync(string token, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "/auth-api/v0/users/current", json: null, token, cancellationToken);

    public Task<JsonElement> GetSummaryAsync(string token, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "/api/v0/users/get_user_summary", json: null, token, cancellationToken);

    public Task<JsonElement> LogoutAsync(string token, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, "/auth-api/v0/users/logout", json: null, token, cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> PostAsync(string method, JsonObject? body, string? token, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Post, $"/auth-api/v0/dsh/{method}", body?.ToJsonString(), token, cancellationToken);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string? json, string? token, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_timeoutMs);
        using var message = new HttpRequestMessage(method, _origin + path);
        AddClientHeaders(message);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token is not null)
            message.Headers.TryAddWithoutValidation("x-dsh-auth-token", token);
        _log?.Invoke($"request {method.Method} {path}");
        var response = await SendWithDiagnosticsAsync(message, path, attempt.Token, cancellationToken);
        using (response)
            return await ReadResponseAsync(response, path, token, attempt.Token);
    }

    private async Task<HttpResponseMessage> SendWithDiagnosticsAsync(HttpRequestMessage message, string path,
        CancellationToken attemptToken, CancellationToken callerToken)
    {
        try
        {
            return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attemptToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            _log?.Invoke($"request failed {path} failure=network");
            throw new AccountException(AccountFailure.Network);
        }
        catch (HttpRequestException)
        {
            _log?.Invoke($"request failed {path} failure=network");
            throw new AccountException(AccountFailure.Network);
        }
    }

    private async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, string path, string? token, CancellationToken cancellationToken)
    {
        _log?.Invoke($"response {path} status={(int)response.StatusCode}");
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            _log?.Invoke($"response rejected {path} stage=redirect failure=protocol");
            throw new AccountException(AccountFailure.Protocol);
        }
        if (status == 401 && token is not null)
        {
            _log?.Invoke($"response rejected {path} failure=expired");
            throw new AccountException(AccountFailure.Expired);
        }
        if (!response.IsSuccessStatusCode)
        {
            _log?.Invoke($"response rejected {path} stage=http failure=network");
            throw new AccountException(AccountFailure.Network);
        }
        return ParseEnvelope(await ReadBoundedAsync(response, path, cancellationToken), path, token);
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaxResponseBytes)
            {
                _log?.Invoke($"response rejected {path} stage=body-limit failure=protocol");
                throw new AccountException(AccountFailure.Protocol);
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private JsonElement ParseEnvelope(byte[] bytes, string path, string? token)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            _log?.Invoke($"response rejected {path} stage=parse-json failure=protocol");
            throw new AccountException(AccountFailure.Protocol);
        }
        using (document)
        {
            var root = document.RootElement;
            var code = PlatformPayloads.OptionalInt(root, "code");
            var data = root.ValueKind == JsonValueKind.Object ? root.GetPropertyOrNull("data") : null;
            var bizCode = data is { } payload ? PlatformPayloads.OptionalInt(payload, "biz_code") : null;
            _log?.Invoke($"codes {path} code={code?.ToString(CultureInfo.InvariantCulture) ?? "?"} bizCode={bizCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            if (code == 40003 && token is not null)
            {
                _log?.Invoke($"response rejected {path} failure=expired");
                throw new AccountException(AccountFailure.Expired);
            }
            if (code != 0 || data is null || bizCode != 0)
            {
                _log?.Invoke($"response rejected {path} stage=envelope failure=protocol");
                throw new AccountException(AccountFailure.Protocol);
            }
            return data.Value.TryGetProperty("biz_data", out var bizData) ? bizData.Clone() : default;
        }
    }

    private void AddClientHeaders(HttpRequestMessage message)
    {
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalSeconds;
        message.Headers.TryAddWithoutValidation("x-client-bundle-id", "");
        message.Headers.TryAddWithoutValidation("x-client-platform", _clientPlatform);
        message.Headers.TryAddWithoutValidation("x-client-version", _clientVersion);
        message.Headers.TryAddWithoutValidation("x-client-locale", _locale);
        message.Headers.TryAddWithoutValidation("x-client-timezone-offset", offset.ToString(CultureInfo.InvariantCulture));
    }

    private static string RequiredString(JsonElement value, string name)
        => PlatformPayloads.OptionalString(value, name) ?? throw new AccountException(AccountFailure.Protocol);
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
}
