using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dsh.Llm.Anthropic;

public sealed class AnthropicAdapter : LlmAdapter
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly HttpClient _http;
    private readonly ReasoningEffortTable _reasoningEfforts = ReasoningEffortTable.Load();

    public AnthropicAdapter(
        string providerId,
        string baseUrl,
        string? apiKey = null,
        IReadOnlyList<string>? modelIds = null,
        HttpClient? httpClient = null)
    {
        ProviderInfo = new LlmProviderInfo(providerId, "Anthropic");
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _modelIds = modelIds ?? [];
        _http = httpClient ?? new HttpClient();
    }

    public override LlmProviderInfo ProviderInfo { get; }

    public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "anthropic");

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => _modelIds
            .Select(id => new LlmModelInfo(ProviderInfo.Id, id, id, null, ["text"]))
            .ToList();

    public override LlmResolvedModelInfo ResolveModel(string model)
    {
        var reasoning = _reasoningEfforts.Resolve(ProviderInfo.Id, model);
        return new LlmResolvedModelInfo(ProviderInfo.Id, model, model, null, ["text"], null, null, reasoning);
    }

    public override async IAsyncEnumerable<StreamChunk> Stream(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new LlmException(new LlmFailure(
                "Anthropic API key is missing",
                LlmFailureCodes.MissingCredential));

        var payload = AnthropicWire.SerializeRequest(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new LlmException(new LlmFailure(
                $"Anthropic API request to {_baseUrl} failed: {error.Message}",
                LlmFailureCodes.Transport), error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new LlmException(
                    BuildHttpFailure(response, rawResponse),
                    new Exception(rawResponse.Length > 0 ? rawResponse : $"Anthropic HTTP {(int)response.StatusCode}"));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var chunk in AnthropicSse.Translate(stream, cancellationToken))
                yield return chunk;
        }
    }

    private LlmFailure BuildHttpFailure(HttpResponseMessage response, string rawResponse)
    {
        var status = (int)response.StatusCode;
        var message = $"Anthropic API error (HTTP {status})";
        string? type = null;
        string? code = null;

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var messageProperty))
                    message = messageProperty.GetString() ?? message;
                if (error.TryGetProperty("type", out var typeProperty))
                    type = typeProperty.GetString();
                if (error.TryGetProperty("code", out var codeProperty))
                    code = codeProperty.GetString();
            }
        }
        catch (JsonException)
        {
        }

        var delay = RetryAfterMs(response.Headers.TryGetValues("retry-after", out var values)
            ? values.FirstOrDefault()
            : null);
        var requestId = response.Headers.TryGetValues("request-id", out var ids)
                        && ids.FirstOrDefault() is { Length: > 0 } id
            ? ProviderRequestId.Create(id)
            : (ProviderRequestId?)null;

        return new LlmFailure(
            message,
            ErrorCode(status, type, code, message),
            status,
            delay,
            requestId);
    }

    private static string ErrorCode(int status, string? type, string? code, string message)
    {
        if (status is 401 or 403)
            return LlmFailureCodes.InvalidCredential;

        var detail = string.Join(' ', new[] { type, code, message }.Where(field => !string.IsNullOrEmpty(field)));

        if (LlmFailureClassifiers.IsQuotaExceededError(detail))
            return LlmFailureCodes.Quota;
        if (status == 429)
            return LlmFailureCodes.RateLimit;
        if (status == 400)
        {
            if (LlmFailureClassifiers.IsContextWindowExceededError(detail))
                return LlmFailureCodes.ContextWindowExceeded;
            return "INVALID_REQUEST";
        }
        if (status >= 500)
            return LlmFailureCodes.Server;
        return $"HTTP_{status}";
    }

    private static long? RetryAfterMs(string? value)
    {
        if (value is null)
            return null;
        if (long.TryParse(value, out var seconds))
        {
            var delay = seconds * 1_000;
            return delay > 0 ? delay : null;
        }
        if (DateTimeOffset.TryParse(value, out var date))
        {
            var delay = (long)(date - DateTimeOffset.UtcNow).TotalMilliseconds;
            return delay > 0 ? delay : null;
        }
        return null;
    }
}
