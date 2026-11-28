using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsh.Core;

namespace Dsh.Channels;

internal sealed record WebhookHandleResult(int StatusCode, string JsonBody);

public sealed class WebhookChannelAdapter(ExternalChannelRouter router, Func<HttpClient> httpFactory) : IExternalChannelAdapter
{
    public const string Kind = "github";
    private const string SignaturePrefix = "sha256=";

    public string AdapterKind => Kind;

    public async Task SendAsync(ChannelBinding binding, ChannelOutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binding.OutboundUrl))
            throw new InvalidOperationException($"Binding '{binding.BindingId}' does not have an OutboundUrl configured.");

        using var client = httpFactory();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DiscordOutboundPayload(message.Body),
            DshChannelsJsonContext.Default.DiscordOutboundPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, binding.OutboundUrl)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<WebhookHandleResult> HandleInboundAsync(
        IReadOnlyDictionary<string, string> headers,
        byte[] rawBody,
        CancellationToken cancellationToken)
    {
        headers.TryGetValue("x-github-event", out var ghEvent);
        var eventName = string.IsNullOrWhiteSpace(ghEvent) ? "push" : ghEvent.Trim();
        headers.TryGetValue("x-github-delivery", out var deliveryId);
        headers.TryGetValue("x-hub-signature-256", out var signature);
        headers.TryGetValue("x-dsh-binding-id", out var explicitBindingId);

        string? repository = null;
        string? action = null;
        string? sender = null;
        string summary;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            repository = TryPathString(root, "repository", "full_name");
            action = TryPathString(root, "action");
            sender = TryPathString(root, "sender", "login");
            summary = BuildGitHubSummary(root, eventName, repository, action, sender);
        }
        catch (JsonException)
        {
            return new WebhookHandleResult(400, JsonError("Invalid JSON payload."));
        }

        var candidates = router.FindBindings(Kind, repository, explicitBindingId);
        var verifiedBinding = VerifyMatchingBinding(candidates, rawBody, signature);
        if (verifiedBinding is null)
            return new WebhookHandleResult(401, JsonError("Missing or invalid X-Hub-Signature-256."));

        var inbound = new ChannelInboundMessage(
            verifiedBinding.BindingId,
            Kind,
            repository ?? verifiedBinding.ChannelKey,
            string.IsNullOrWhiteSpace(deliveryId) ? $"gh-{Guid.NewGuid():N}" : deliveryId,
            sender ?? "github",
            sender ?? "GitHub",
            summary,
            router.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            repository,
            eventName,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-dsh-signature-verified"] = "true",
                ["x-github-event"] = eventName,
            });

        var triggersFired = await router.FireTriggerAsync(Kind, eventName, summary, cancellationToken).ConfigureAwait(false);
        var delivery = await router.DispatchInboundAsync(inbound, cancellationToken).ConfigureAwait(false);

        var ack = JsonSerializer.Serialize(
            new WebhookAckResponse(true, delivery.Status.ToString(), delivery.EnvelopeId, triggersFired),
            DshChannelsJsonContext.Default.WebhookAckResponse);
        return new WebhookHandleResult(200, ack);
    }

    public static bool VerifySignature(string secret, ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;
        var trimmed = signatureHeader.Trim();
        if (!trimmed.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var hex = trimmed.AsSpan(SignaturePrefix.Length);
        if (hex.Length != 64)
            return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private ChannelBinding? VerifyMatchingBinding(IReadOnlyList<ChannelBinding> candidates, byte[] rawBody, string? signature)
    {
        foreach (var binding in candidates)
        {
            var secret = router.ResolveSecret(binding);
            if (!string.IsNullOrEmpty(secret) && VerifySignature(secret, rawBody, signature))
                return binding;
        }
        return null;
    }

    private static string BuildGitHubSummary(JsonElement root, string eventName, string? repository, string? action, string? sender)
    {
        var detail = TryPathString(root, "workflow_run", "conclusion") is { } conclusion
            ? $"{TryPathString(root, "workflow_run", "name") ?? "workflow"}: {conclusion}"
            : TryPathString(root, "pull_request", "title")
            ?? TryPathString(root, "head_commit", "message")
            ?? TryPathString(root, "issue", "title")
            ?? TryPathString(root, "comment", "body")
            ?? "";

        var prefix = repository is null ? $"GitHub {eventName}" : $"GitHub {eventName} on {repository}";
        if (!string.IsNullOrWhiteSpace(action))
            prefix = $"{prefix} ({action})";
        if (!string.IsNullOrWhiteSpace(sender))
            prefix = $"{prefix} by {sender}";
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
    }

    private static string? TryPathString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string JsonError(string message)
        => JsonSerializer.Serialize(new WebhookAckResponse(false, Error: message), DshChannelsJsonContext.Default.WebhookAckResponse);
}

public sealed class SlackChannelAdapter(ExternalChannelRouter router, Func<HttpClient> httpFactory) : IExternalChannelAdapter
{
    public const string Kind = "slack";
    private const string SignatureVersionPrefix = "v0=";
    private const long ReplayWindowSeconds = 300;

    public string AdapterKind => Kind;

    public async Task SendAsync(ChannelBinding binding, ChannelOutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binding.OutboundUrl))
            throw new InvalidOperationException($"Binding '{binding.BindingId}' does not have an OutboundUrl configured.");

        using var client = httpFactory();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SlackOutboundPayload(binding.ChannelKey, message.ThreadId, message.Body),
            DshChannelsJsonContext.Default.SlackOutboundPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, binding.OutboundUrl)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (router.ResolveBotToken(binding, Kind) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<WebhookHandleResult> HandleInboundAsync(
        IReadOnlyDictionary<string, string> headers,
        byte[] rawBody,
        CancellationToken cancellationToken)
    {
        headers.TryGetValue("x-slack-request-timestamp", out var timestampHeader);
        headers.TryGetValue("x-slack-signature", out var signatureHeader);
        headers.TryGetValue("x-dsh-binding-id", out var explicitBindingId);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return new WebhookHandleResult(400, JsonError("Invalid JSON payload."));
        }

        using (doc)
        {
            var root = doc.RootElement;
            var payloadType = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;

            string? channelId = null;
            if (root.TryGetProperty("event", out var eventEl) && eventEl.ValueKind == JsonValueKind.Object
                && eventEl.TryGetProperty("channel", out var chProp) && chProp.ValueKind == JsonValueKind.String)
            {
                channelId = chProp.GetString();
            }

            var candidates = router.FindBindings(Kind, channelId, explicitBindingId);
            if (candidates.Count == 0 && payloadType == "url_verification")
                candidates = router.FindBindings(Kind, null, explicitBindingId);

            var verifiedBinding = VerifyMatchingBinding(candidates, timestampHeader, signatureHeader, rawBody);
            if (verifiedBinding is null)
                return new WebhookHandleResult(401, JsonError("Missing or invalid X-Slack-Signature."));

            if (string.Equals(payloadType, "url_verification", StringComparison.Ordinal))
            {
                var challenge = root.TryGetProperty("challenge", out var ch) && ch.ValueKind == JsonValueKind.String
                    ? ch.GetString() ?? ""
                    : "";
                var responseJson = JsonSerializer.Serialize(
                    new SlackUrlVerificationResponse(challenge),
                    DshChannelsJsonContext.Default.SlackUrlVerificationResponse);
                return new WebhookHandleResult(200, responseJson);
            }

            if (!string.Equals(payloadType, "event_callback", StringComparison.Ordinal)
                || !root.TryGetProperty("event", out var evt)
                || evt.ValueKind != JsonValueKind.Object)
            {
                var ackOther = JsonSerializer.Serialize(new WebhookAckResponse(true, "ignored"), DshChannelsJsonContext.Default.WebhookAckResponse);
                return new WebhookHandleResult(200, ackOther);
            }

            var text = GetString(evt, "text") ?? "";
            var user = GetString(evt, "user") ?? GetString(evt, "bot_id") ?? "slack-user";
            var ts = GetString(evt, "ts") ?? "";
            var threadTs = GetString(evt, "thread_ts") ?? (string.IsNullOrEmpty(ts) ? null : ts);
            var eventId = GetString(root, "event_id") ?? ts;
            var eventType = GetString(evt, "type") ?? "message";

            var inbound = new ChannelInboundMessage(
                verifiedBinding.BindingId,
                Kind,
                channelId ?? verifiedBinding.ChannelKey,
                string.IsNullOrWhiteSpace(eventId) ? $"slack-{Guid.NewGuid():N}" : eventId,
                user,
                user,
                text,
                router.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                threadTs,
                eventType,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x-dsh-signature-verified"] = "true",
                });

            var result = await router.DispatchInboundAsync(inbound, cancellationToken).ConfigureAwait(false);
            var ack = JsonSerializer.Serialize(
                new WebhookAckResponse(true, result.Status.ToString(), result.EnvelopeId),
                DshChannelsJsonContext.Default.WebhookAckResponse);
            return new WebhookHandleResult(200, ack);
        }
    }

    public static bool VerifySignature(
        string secret,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret)
            || string.IsNullOrWhiteSpace(timestampHeader)
            || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        if (!long.TryParse(timestampHeader.Trim(), out var timestamp))
            return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > ReplayWindowSeconds)
            return false;

        var trimmed = signatureHeader.Trim();
        if (!trimmed.StartsWith(SignatureVersionPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var hex = trimmed.AsSpan(SignatureVersionPrefix.Length);
        if (hex.Length != 64)
            return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        var prefixBytes = Encoding.UTF8.GetBytes($"v0:{timestamp}:");
        var signedPayload = new byte[prefixBytes.Length + rawBody.Length];
        prefixBytes.CopyTo(signedPayload, 0);
        rawBody.CopyTo(signedPayload.AsSpan(prefixBytes.Length));

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedPayload, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private ChannelBinding? VerifyMatchingBinding(
        IReadOnlyList<ChannelBinding> candidates,
        string? timestampHeader,
        string? signatureHeader,
        byte[] rawBody)
    {
        var now = router.Clock.GetUtcNow();
        foreach (var binding in candidates)
        {
            var secret = router.ResolveSecret(binding);
            if (!string.IsNullOrEmpty(secret) && VerifySignature(secret, timestampHeader, signatureHeader, rawBody, now))
                return binding;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string JsonError(string message)
        => JsonSerializer.Serialize(new WebhookAckResponse(false, Error: message), DshChannelsJsonContext.Default.WebhookAckResponse);
}

public sealed class DiscordChannelAdapter(ExternalChannelRouter router, Func<HttpClient> httpFactory) : IExternalChannelAdapter
{
    public const string Kind = "discord";

    public string AdapterKind => Kind;

    public async Task SendAsync(ChannelBinding binding, ChannelOutboundMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binding.OutboundUrl))
            throw new InvalidOperationException($"Binding '{binding.BindingId}' does not have an OutboundUrl configured.");

        using var client = httpFactory();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DiscordOutboundPayload(message.Body),
            DshChannelsJsonContext.Default.DiscordOutboundPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, binding.OutboundUrl)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (router.ResolveBotToken(binding, Kind) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<WebhookHandleResult> HandleInboundAsync(
        IReadOnlyDictionary<string, string> headers,
        byte[] rawBody,
        CancellationToken cancellationToken)
    {
        headers.TryGetValue("x-dsh-binding-id", out var explicitBindingId);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return new WebhookHandleResult(400, JsonError("Invalid JSON payload."));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.Number && typeProp.GetInt32() == 1)
            {
                var pong = JsonSerializer.Serialize(new DiscordPongResponse(1), DshChannelsJsonContext.Default.DiscordPongResponse);
                return new WebhookHandleResult(200, pong);
            }

            var channelId = GetString(root, "channel_id") ?? GetString(root, "channelId");
            var candidates = router.FindBindings(Kind, channelId, explicitBindingId);
            var binding = MatchAuthorizedBinding(candidates, headers, rawBody);
            if (binding is null)
                return new WebhookHandleResult(401, JsonError("No matching Discord binding or invalid webhook token."));

            var (body, eventType) = ExtractDiscordBody(root);
            var messageId = GetString(root, "id") ?? $"discord-{Guid.NewGuid():N}";
            var threadId = GetString(root, "thread_id")
                ?? TryNestedString(root, "message_reference", "message_id")
                ?? channelId
                ?? messageId;

            var senderId = TryNestedString(root, "author", "id")
                ?? TryNestedString(root, "member", "user", "id")
                ?? TryNestedString(root, "user", "id")
                ?? "discord-user";
            var senderName = TryNestedString(root, "author", "username")
                ?? TryNestedString(root, "member", "user", "username")
                ?? TryNestedString(root, "user", "username")
                ?? senderId;

            var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(router.ResolveSecret(binding)))
                inboundHeaders["x-dsh-signature-verified"] = "true";

            var inbound = new ChannelInboundMessage(
                binding.BindingId,
                Kind,
                channelId ?? binding.ChannelKey,
                messageId,
                senderId,
                senderName,
                body,
                router.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                threadId,
                eventType,
                inboundHeaders);

            var result = await router.DispatchInboundAsync(inbound, cancellationToken).ConfigureAwait(false);
            var ack = JsonSerializer.Serialize(
                new WebhookAckResponse(true, result.Status.ToString(), result.EnvelopeId),
                DshChannelsJsonContext.Default.WebhookAckResponse);
            return new WebhookHandleResult(200, ack);
        }
    }

    private ChannelBinding? MatchAuthorizedBinding(
        IReadOnlyList<ChannelBinding> candidates,
        IReadOnlyDictionary<string, string> headers,
        byte[] rawBody)
    {
        foreach (var binding in candidates)
        {
            var secret = router.ResolveSecret(binding);
            if (string.IsNullOrEmpty(secret))
                return binding;

            if (headers.TryGetValue("x-hub-signature-256", out var sig)
                && WebhookChannelAdapter.VerifySignature(secret, rawBody, sig))
                return binding;

            if (headers.TryGetValue("x-webhook-token", out var token)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(token)))
                return binding;
        }
        return null;
    }

    private static (string Body, string EventType) ExtractDiscordBody(JsonElement root)
    {
        if (GetString(root, "content") is { Length: > 0 } content)
            return (content, "message");

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var commandName = GetString(data, "name") ?? "interaction";
            if (data.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var opt in options.EnumerateArray())
                {
                    if (GetString(opt, "name") is { } optName && opt.TryGetProperty("value", out var val))
                        parts.Add($"{optName}={val}");
                }
                if (parts.Count > 0)
                    return ($"/{commandName} {string.Join(' ', parts)}", "interaction");
            }
            return ($"/{commandName}", "interaction");
        }

        return ("", "webhook");
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string JsonError(string message)
        => JsonSerializer.Serialize(new WebhookAckResponse(false, Error: message), DshChannelsJsonContext.Default.WebhookAckResponse);
}
