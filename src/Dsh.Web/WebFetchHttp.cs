using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Cordis;
using Dsh.Llm;

namespace Dsh.Web;

public sealed record PublicAddress(string Address, int Family);

public sealed record HttpFetchLimits
{
    public int MaxResponseBytes { get; init; } = 5_000_000;
    public int MaxBodyChars { get; init; } = 100_000;
    public long TimeoutMs { get; init; } = 30_000;
    public int MaxRedirects { get; init; } = 5;
    public string UserAgent { get; init; } = "deepseek-harness/0.0.1 (+https://github.com/deepseek-ai)";
}

public sealed class WebFetchHttpConfig
{
    public int MaxResponseBytes { get; init; } = 5_000_000;
    public int MaxBodyChars { get; init; } = 100_000;
    public long TimeoutMs { get; init; } = 30_000;
    public int MaxRedirects { get; init; } = 5;
    public string UserAgent { get; init; } = "deepseek-harness/0.0.1 (+https://github.com/deepseek-ai)";
}

public static class WebFetchHttpPolicy
{
    public const int MaxUrlLength = 2048;
    public const string LocalFetchProviderId = "http";

    public static Uri ParseFetchUrl(string input)
    {
        Uri url;
        try
        {
            url = new Uri(input);
        }
        catch (Exception error)
        {
            throw new WebError($"invalid URL: {input}", WebErrorCodes.InvalidUrl, error);
        }
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new WebError($"unsupported URL scheme \"{url.Scheme}:\" (only http and https are allowed)", WebErrorCodes.InvalidUrl);
        }
        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            throw new WebError("credentials in URLs are not allowed", WebErrorCodes.BlockedUrl);
        }
        return url;
    }

    public static Uri ValidateFetchUrl(string input)
    {
        if (input.Length > MaxUrlLength)
            throw new WebError($"URL exceeds the maximum length of {MaxUrlLength}", WebErrorCodes.InvalidUrl);
        return ParseFetchUrl(input);
    }

    public static bool IsSameOrigin(Uri a, Uri b)
        => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;

    public static string? ClassifyContentType(string? contentType)
    {
        var mime = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (mime == "text/html" || mime == "application/xhtml+xml")
            return "html";
        if (mime.StartsWith("text/", StringComparison.Ordinal))
            return "text";
        if (mime == "application/json" || mime == "application/xml" || mime.EndsWith("+json", StringComparison.Ordinal) || mime.EndsWith("+xml", StringComparison.Ordinal))
            return "text";
        return null;
    }

    public static string? ParseCharset(string? contentType)
    {
        if (contentType is null)
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(contentType, @";\s*charset\s*=\s*""?([^"";]+)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().ToLowerInvariant() : null;
    }

    public static Encoding DecoderForCharset(string? charset)
    {
        if (charset is null)
            return Encoding.UTF8;
        if (charset is "iso-8859-1" or "latin1")
            return Encoding.Latin1;
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (Exception error)
        {
            throw new WebError($"unsupported charset \"{charset}\"", WebErrorCodes.UnsupportedContentType, error);
        }
    }
}

public static class WebFetchHttpNetwork
{
    public static bool IsPublicIpAddress(string input)
    {
        if (!IPAddress.TryParse(input, out var address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return IsPublicIpv4(bytes);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPublicIpAddress(address.MapToIPv4().ToString());
            return IsPublicIpv6(address);
        }
        return false;
    }

    public static async Task<IReadOnlyList<PublicAddress>> ResolvePublicAddresses(string hostname, CancellationToken signal, Func<string, CancellationToken, Task<IReadOnlyList<PublicAddress>>>? resolver = null)
    {
        var unbracketed = StripIpv6Brackets(hostname);
        var literalFamily = GetIpFamily(unbracketed);
        IReadOnlyList<PublicAddress> resolved;
        if (literalFamily == 0)
        {
            resolved = resolver is not null
                ? await resolver(unbracketed, signal).ConfigureAwait(false)
                : await ResolveSystem(unbracketed, signal).ConfigureAwait(false);
        }
        else
        {
            resolved = [new PublicAddress(unbracketed, literalFamily)];
        }

        if (resolved.Count == 0)
            throw new WebError($"hostname \"{hostname}\" resolved to no addresses", WebErrorCodes.ProviderError);

        foreach (var entry in resolved)
        {
            if ((entry.Family != 4 && entry.Family != 6) || GetIpFamily(entry.Address) != entry.Family)
                throw new WebError($"hostname \"{hostname}\" resolved to an invalid IP address", WebErrorCodes.ProviderError);
            if (!IsPublicIpAddress(entry.Address))
                throw new WebError($"URL hostname \"{hostname}\" resolves to a non-public IP address", WebErrorCodes.BlockedUrl);
        }
        return resolved;
    }

    public static bool IsNonPublicIpLiteral(string hostname)
    {
        var unbracketed = StripIpv6Brackets(hostname);
        return GetIpFamily(unbracketed) != 0 && !IsPublicIpAddress(unbracketed);
    }

    private static async Task<IReadOnlyList<PublicAddress>> ResolveSystem(string hostname, CancellationToken signal)
    {
        var addresses = await Dns.GetHostAddressesAsync(hostname, signal).ConfigureAwait(false);
        return addresses
            .Select(address => new PublicAddress(address.ToString(), address.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4))
            .ToList();
    }

    private static int GetIpFamily(string input)
    {
        if (IPAddress.TryParse(input, out var address))
            return address.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4;
        return 0;
    }

    private static string StripIpv6Brackets(string hostname)
        => hostname.StartsWith('[') && hostname.EndsWith(']') ? hostname[1..^1] : hostname;

    private static bool IsPublicIpv4(byte[] bytes)
    {
        var first = bytes[0];
        if (first == 0 || first == 10 || first == 127)
            return false;
        if (first == 100 && bytes[1] is >= 64 and <= 127)
            return false;
        if (first == 169 && bytes[1] == 254)
            return false;
        if (first == 172 && bytes[1] is >= 16 and <= 31)
            return false;
        if (first == 192 && bytes[1] == 168)
            return false;
        if (first >= 224)
            return false;
        if (first == 192 && bytes[1] == 0 && bytes[2] == 2)
            return false;
        if (first == 198 && bytes[1] == 51 && bytes[2] == 100)
            return false;
        if (first == 203 && bytes[1] == 0 && bytes[2] == 113)
            return false;
        return true;
    }

    private static bool IsPublicIpv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None))
            return false;
        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
            return false;
        if ((bytes[0] & 0xFF) == 0xFF)
            return false;
        if ((bytes[0] & 0xFE) == 0xFC || (bytes[0] & 0xFF) == 0xFE && (bytes[1] & 0xC0) == 0x80)
            return false;
        return true;
    }
}

public sealed class HttpFetchProvider : IWebFetchProvider
{
    private readonly HttpFetchLimits _limits;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<PublicAddress>>> _resolveAddresses;

    public HttpFetchProvider(HttpFetchLimits? limits = null, Func<string, CancellationToken, Task<IReadOnlyList<PublicAddress>>>? resolveAddresses = null)
    {
        _limits = limits ?? new HttpFetchLimits();
        _resolveAddresses = resolveAddresses ?? ((host, token) => WebFetchHttpNetwork.ResolvePublicAddresses(host, token));
    }

    public string Id => WebFetchHttpPolicy.LocalFetchProviderId;

    public bool Available() => true;

    public async Task<WebFetchResult> Fetch(WebFetchRequest request, CancellationToken signal = default)
    {
        if (signal.IsCancellationRequested)
            throw new WebError("web fetch aborted", WebErrorCodes.Aborted);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(signal);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(_limits.TimeoutMs));
        return await FollowAndRead(request.Url, timeoutSource.Token, signal).ConfigureAwait(false);
    }

    private async Task<WebFetchResult> FollowAndRead(string initialUrl, CancellationToken signal, CancellationToken callerSignal)
    {
        var currentUrl = WebFetchHttpPolicy.ValidateFetchUrl(initialUrl);
        var redirectsFollowed = 0;
        while (true)
        {
            using var request = await RequestOnce(currentUrl, signal, callerSignal).ConfigureAwait(false);
            var response = request.Response;
            if (IsRedirectStatus((int)response.StatusCode))
            {
                if (redirectsFollowed >= _limits.MaxRedirects)
                {
                    throw new WebError($"exceeded the maximum of {_limits.MaxRedirects} redirects", WebErrorCodes.RedirectBlocked);
                }
                var location = LocationHeader(response);
                if (location is null)
                {
                    throw new WebError($"redirect response (HTTP {(int)response.StatusCode}) without a Location header", WebErrorCodes.ProviderError);
                }
                Uri target;
                try
                {
                    target = new Uri(currentUrl, location);
                }
                catch (Exception error)
                {
                    throw new WebError($"invalid redirect Location \"{location}\"", WebErrorCodes.ProviderError, error);
                }
                try
                {
                    var validated = WebFetchHttpPolicy.ValidateFetchUrl(target.ToString());
                    if (!WebFetchHttpPolicy.IsSameOrigin(validated, currentUrl))
                    {
                        throw new WebError(
                            $"cross-origin redirect to {validated.GetLeftPart(UriPartial.Authority)} is not followed automatically; retry against that URL directly",
                            WebErrorCodes.RedirectBlocked);
                    }
                    currentUrl = validated;
                }
                catch (WebError)
                {
                    throw;
                }
                redirectsFollowed += 1;
                continue;
            }
            return await ReadBody(response, currentUrl, signal, callerSignal).ConfigureAwait(false);
        }
    }

    private sealed record PinnedHttpResponse(HttpResponseMessage Response, HttpClient Client) : IDisposable
    {
        public void Dispose()
        {
            Response.Dispose();
            Client.Dispose();
        }
    }

    private static string? LocationHeader(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault() : null;
    }

    private async Task<PinnedHttpResponse> RequestOnce(Uri url, CancellationToken signal, CancellationToken callerSignal)
    {
        try
        {
            var addresses = await _resolveAddresses(url.Host, signal).ConfigureAwait(false);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = (context, token) => ConnectPinnedAsync(addresses, context.DnsEndPoint.Port, token),
            };
            var client = new HttpClient(handler, disposeHandler: true);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", _limits.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/*;q=0.9,application/json;q=0.8");
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, signal).ConfigureAwait(false);
                return new PinnedHttpResponse(response, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (!callerSignal.IsCancellationRequested && signal.IsCancellationRequested)
        {
            throw new WebError("web fetch timed out", WebErrorCodes.FetchTimeout);
        }
        catch (OperationCanceledException)
        {
            throw new WebError("web fetch aborted", WebErrorCodes.Aborted);
        }
        catch (WebError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new WebError($"web fetch failed: {error.Message}", WebErrorCodes.ProviderError, error);
        }
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(IReadOnlyList<PublicAddress> addresses, int port, CancellationToken signal)
    {
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address.Address), port, signal).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error)
            {
                lastError = error;
                socket.Dispose();
            }
        }
        throw lastError ?? new SocketException((int)SocketError.AddressNotAvailable);
    }

    private async Task<WebFetchResult> ReadBody(HttpResponseMessage response, Uri finalUrl, CancellationToken signal, CancellationToken callerSignal)
    {
        var contentType = response.Content.Headers.ContentType?.ToString();
        var kind = WebFetchHttpPolicy.ClassifyContentType(contentType);
        if (kind is null)
            throw new WebError($"unsupported content type \"{contentType ?? "unknown"}\"", WebErrorCodes.UnsupportedContentType);

        Encoding encoding;
        try
        {
            encoding = WebFetchHttpPolicy.DecoderForCharset(WebFetchHttpPolicy.ParseCharset(contentType));
        }
        catch (WebError)
        {
            throw;
        }

        var (bytes, truncatedByBytes) = await ReadCappedAsync(response, signal, callerSignal).ConfigureAwait(false);
        var decoded = encoding.GetString(bytes);
        var truncatedByChars = decoded.Length > _limits.MaxBodyChars;
        var content = truncatedByChars ? decoded[.._limits.MaxBodyChars] : decoded;
        var body = new WebFetchBody(kind, content);
        return new WebFetchResult(finalUrl.ToString(), (int)response.StatusCode, body, truncatedByBytes || truncatedByChars);
    }

    private async Task<(byte[] Bytes, bool TruncatedByBytes)> ReadCappedAsync(HttpResponseMessage response, CancellationToken signal, CancellationToken callerSignal)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > _limits.MaxResponseBytes)
            throw new WebError($"response exceeds the maximum of {_limits.MaxResponseBytes} bytes", WebErrorCodes.FetchTooLarge);

        await using var stream = await response.Content.ReadAsStreamAsync(signal).ConfigureAwait(false);
        var chunks = new List<byte[]>();
        var total = 0;
        var truncated = false;
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, signal).ConfigureAwait(false);
                if (read == 0)
                    break;
                var remaining = _limits.MaxResponseBytes - total;
                if (read > remaining)
                {
                    var chunk = new byte[Math.Max(0, remaining)];
                    if (chunk.Length > 0)
                        Array.Copy(buffer, chunk, chunk.Length);
                    chunks.Add(chunk);
                    total += chunk.Length;
                    truncated = true;
                    break;
                }
                var fullChunk = new byte[read];
                Array.Copy(buffer, fullChunk, read);
                chunks.Add(fullChunk);
                total += read;
            }
        }
        catch (OperationCanceledException) when (!callerSignal.IsCancellationRequested && signal.IsCancellationRequested)
        {
            throw new WebError("web fetch timed out", WebErrorCodes.FetchTimeout);
        }
        catch (OperationCanceledException)
        {
            throw new WebError("web fetch aborted", WebErrorCodes.Aborted);
        }

        var bytes = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, bytes, offset, chunk.Length);
            offset += chunk.Length;
        }
        return (bytes, truncated);
    }

    private static bool IsRedirectStatus(int status)
        => status is 301 or 302 or 303 or 307 or 308;
}

public static class WebFetchHttp
{
    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var resolved = config switch
        {
            WebFetchHttpConfig typed => typed,
            null => new WebFetchHttpConfig(),
            IReadOnlyDictionary<string, object?> dict => FromDictionary(dict),
            _ => throw new ArgumentException("web-fetch-http config must be an object or WebFetchHttpConfig", nameof(config)),
        };
        AssertPositiveFinite("maxResponseBytes", resolved.MaxResponseBytes);
        AssertPositiveFinite("maxBodyChars", resolved.MaxBodyChars);
        AssertPositiveFinite("timeoutMs", resolved.TimeoutMs);
        AssertNonNegativeInteger("maxRedirects", resolved.MaxRedirects);
        if (resolved.TimeoutMs > int.MaxValue)
            throw new ArgumentException($"web-fetch-http: timeoutMs must be no greater than {int.MaxValue}");
        var web = ctx.Get<WebRuntime>(WebRuntime.ServiceName)
            ?? throw new InvalidOperationException("web service is not registered");
        var limits = new HttpFetchLimits
        {
            MaxResponseBytes = resolved.MaxResponseBytes,
            MaxBodyChars = resolved.MaxBodyChars,
            TimeoutMs = resolved.TimeoutMs,
            MaxRedirects = resolved.MaxRedirects,
            UserAgent = resolved.UserAgent,
        };
        return web.RegisterFetchProvider(new HttpFetchProvider(limits));
    }

    private static WebFetchHttpConfig FromDictionary(IReadOnlyDictionary<string, object?> dict)
    {
        return new WebFetchHttpConfig
        {
            MaxResponseBytes = IntOf(dict, "maxResponseBytes") ?? new WebFetchHttpConfig().MaxResponseBytes,
            MaxBodyChars = IntOf(dict, "maxBodyChars") ?? new WebFetchHttpConfig().MaxBodyChars,
            TimeoutMs = LongOf(dict, "timeoutMs") ?? new WebFetchHttpConfig().TimeoutMs,
            MaxRedirects = IntOf(dict, "maxRedirects") ?? new WebFetchHttpConfig().MaxRedirects,
            UserAgent = dict.GetValueOrDefault("userAgent") as string ?? new WebFetchHttpConfig().UserAgent,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (int)value,
            _ => null,
        };

    private static long? LongOf(IReadOnlyDictionary<string, object?> dict, string key)
        => dict.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            double value when double.IsFinite(value) && value % 1 == 0 => (long)value,
            _ => null,
        };

    private static void AssertPositiveFinite(string name, double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentException($"web-fetch-http: {name} must be a positive finite number");
    }

    private static void AssertNonNegativeInteger(string name, int value)
    {
        if (value < 0)
            throw new ArgumentException($"web-fetch-http: {name} must be a non-negative integer");
    }
}
