using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsh.Runtime;

/// <summary>
/// Process-local outbound routing.
/// Configuration changes retire the previous generation; handlers reuse connection pools while a route is unchanged.
/// </summary>
public sealed partial class OutboundProxy : IWebProxy, ICredentials, IDisposable
{
    public const string PropName = "outboundProxy";

    private const string DefaultScheme = "http";
    private const string RedactedUserInfo = "***@";
    private const string MatchAll = "*";
    private const string SystemProxyLabel = "系统代理";

    private static readonly HashSet<string> SupportedSchemes = new(["http", "https", "socks4", "socks4a", "socks5"], StringComparer.OrdinalIgnoreCase);

    private static readonly IPNetwork[] LocalNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    private readonly object _gate = new();
    private RouteGeneration _route = new(0, new Route(null, null, []));
    private Route _configured = new(null, null, []);
    private Route? _borrowed;
    private Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? _borrowedConnect;
    private bool _borrowedSuspended;
    private bool _disposed;

    internal sealed record Route(Uri? Address, CredentialHolder? Credential, string[] Bypass)
    {
        internal Route CopyForGeneration()
            => Credential is null ? this : new Route(Address, Credential.CopyForGeneration(), Bypass);

        internal bool EquivalentTo(Route other)
            => Address == other.Address
                && CredentialHolder.Equivalent(Credential, other.Credential)
                && Bypass.AsSpan().SequenceEqual(other.Bypass);
    }

    internal sealed class RouteGeneration
    {
        internal RouteGeneration(long number, Route configuration,
            Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect = null,
            Action<RouteGeneration>? transportFailed = null, bool unavailable = false)
        {
            Number = number;
            Configuration = configuration;
            Connect = connect;
            _transportFailed = transportFailed;
            IsUnavailable = unavailable;
            if (configuration.Credential is { } credential)
                Lifetime.Register(credential);
        }

        private readonly Action<RouteGeneration>? _transportFailed;
        internal Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? Connect { get; }
        internal void ReportTransportFailure() => _transportFailed?.Invoke(this);

        internal long Number { get; }
        internal Route Configuration { get; }
        internal OutboundResourceLifetime Lifetime { get; } = new();
        internal bool IsUnavailable { get; }

        internal void ThrowIfUnavailable()
        {
            if (IsUnavailable)
                throw new IOException("The selected outbound route is unavailable; acquire a consumer lease or correct and reload its saved selection.");
        }
    }

    /** ctx 里没有登记时(未经 ConfigBoot 的测试或工具)返回一份跟随系统代理的独立实例。 */
    public static OutboundProxy FromContext(Context ctx) => ctx.GetProp(PropName) as OutboundProxy ?? new OutboundProxy();

    /** Active proxy address without credentials; null also covers a suspended route, so status surfaces use Describe(). */
    public Uri? Address => Volatile.Read(ref _route).Configuration.Address;

    /** 脱敏后的路由描述, 日志、错误与界面只用它。 */
    public string Describe()
    {
        var generation = Volatile.Read(ref _route);
        if (generation.IsUnavailable)
            return "Borrowed route unavailable";
        var route = generation.Configuration;
        if (route.Address is not { } address)
            return SystemProxyLabel;
        var userInfo = route.Credential is null ? "" : RedactedUserInfo;
        return $"{address.Scheme}://{userInfo}{address.Authority}";
    }

    /// <summary>Updates only the baseline; a selected borrowed route remains active.</summary>
    public void Configure(string? url, IReadOnlyList<string>? bypass)
    {
        var configuration = string.IsNullOrWhiteSpace(url)
            ? new Route(null, null, [])
            : Parse(url.Trim(), bypass ?? []);
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            if (_disposed)
            {
                configuration.Credential?.Dispose();
                throw new ObjectDisposedException(nameof(OutboundProxy));
            }
            if (_configured.EquivalentTo(configuration))
            {
                configuration.Credential?.Dispose();
                return;
            }
            _configured.Credential?.Dispose();
            _configured = configuration;
            if (_borrowed is not null || _borrowedSuspended)
                return;
            (previous, retired) = ReplaceRouteLocked(configuration, null);
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    /// <summary>Installs an immutable borrowed TCP route; both null values restore the latest baseline.</summary>
    public void ConfigureBorrowed(string? proxyUrl, Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect)
    {
        if ((proxyUrl is null) != (connect is null))
            throw new ArgumentException("The borrowed proxy address and connector must be supplied or cleared together.");
        var configuration = proxyUrl is null ? null : Parse(proxyUrl.Trim(), []);
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            if (_disposed)
            {
                configuration?.Credential?.Dispose();
                throw new ObjectDisposedException(nameof(OutboundProxy));
            }
            if (!_borrowedSuspended
                && (_borrowed is null ? configuration is null : configuration is not null && _borrowed.EquivalentTo(configuration))
                && Equals(_borrowedConnect, connect))
            {
                configuration?.Credential?.Dispose();
                return;
            }
            _borrowed?.Credential?.Dispose();
            _borrowed = configuration;
            _borrowedConnect = connect;
            _borrowedSuspended = false;
            (previous, retired) = ReplaceRouteLocked(configuration ?? _configured, connect);
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    /// <summary>Retires a borrowed generation without revealing the baseline or retaining its credentials.</summary>
    public void SuspendBorrowed()
    {
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_borrowedSuspended)
                return;
            _borrowed?.Credential?.Dispose();
            _borrowed = null;
            _borrowedConnect = null;
            _borrowedSuspended = true;
            (previous, retired) = ReplaceRouteLocked(new Route(null, null, []), null, unavailable: true);
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    private (RouteGeneration Previous, IDisposable[] Retired) ReplaceRouteLocked(
        Route configuration, Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? connect, bool unavailable = false)
    {
        var previous = _route;
        var next = new RouteGeneration(previous.Number + 1, configuration.CopyForGeneration(), connect,
            connect is null ? null : TransportFailed, unavailable);
        var retired = previous.Lifetime.BeginRetirement();
        Volatile.Write(ref _route, next);
        return (previous, retired);
    }

    private void TransportFailed(RouteGeneration generation)
    {
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_route, generation) || _borrowed is null)
                return;
            (previous, retired) = ReplaceRouteLocked(_borrowed, _borrowedConnect);
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    public void Dispose()
    {
        RouteGeneration previous;
        IDisposable[] retired;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            previous = _route;
            retired = previous.Lifetime.BeginRetirement();
            _configured.Credential?.Dispose();
            _borrowed?.Credential?.Dispose();
            _borrowed = null;
            _borrowedConnect = null;
        }
        previous.Lifetime.CompleteRetirement(retired);
    }

    /// <summary>Creates a handler whose sends capture a generation and reuse its connection pools.</summary>
    public HttpMessageHandler CreateHandler(
        bool allowAutoRedirect = true,
        DecompressionMethods automaticDecompression = DecompressionMethods.None)
        => new OutboundHttpHandler(this, allowAutoRedirect, automaticDecompression);

    /// <summary>Captures routing and credentials before asynchronous destination validation begins.</summary>
    public RouteSnapshot CaptureRoute(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var generation = Volatile.Read(ref _route);
        generation.ThrowIfUnavailable();
        generation.Lifetime.ThrowIfRetired();
        var route = generation.Configuration;
        var systemProxy = route.Address is null ? HttpClient.DefaultProxy : null;
        var proxy = EffectiveProxy(route, systemProxy, destination);
        var credentials = route.Credential;
        if (systemProxy is not null && proxy is not null && systemProxy.Credentials is { } source)
        {
            credentials = CredentialHolder.Capture(source, proxy);
            generation.Lifetime.Register(credentials);
        }
        generation.Lifetime.ThrowIfRetired();
        return new RouteSnapshot(generation, proxy, credentials, systemProxy);
    }

    /// <summary>Returns the effective proxy endpoint, or null for a direct route.</summary>
    public Uri? ProxyFor(Uri destination)
    {
        var generation = Volatile.Read(ref _route);
        generation.ThrowIfUnavailable();
        var route = generation.Configuration;
        return EffectiveProxy(route, route.Address is null ? HttpClient.DefaultProxy : null, destination);
    }

    private static Uri? EffectiveProxy(Route route, IWebProxy? systemProxy, Uri destination)
    {
        if (systemProxy is null)
            return Bypassed(route, destination) ? null : route.Address;
        if (systemProxy.IsBypassed(destination))
            return null;
        var proxy = systemProxy.GetProxy(destination);
        return proxy == destination ? null : proxy;
    }

    private static Uri? ProxyForConnection(Route route, IWebProxy? systemProxy, SocketsHttpConnectionContext context)
    {
        var request = context.InitialRequestMessage;
        // SocketsHttpHandler uses the proxy URI for its internal CONNECT request.
        return request.Method == HttpMethod.Connect && request.RequestUri == route.Address
            ? route.Address
            : EffectiveProxy(route, systemProxy, request.RequestUri!);
    }

    /// <summary>An immutable route capture; its token and factories share generation ownership.</summary>
    public readonly struct RouteSnapshot
    {
        private readonly RouteGeneration _generation;
        private readonly CredentialHolder? _credentials;
        private readonly IWebProxy? _systemProxy;

        internal RouteSnapshot(RouteGeneration generation, Uri? proxy, CredentialHolder? credentials, IWebProxy? systemProxy)
        {
            _generation = generation;
            ProxyUri = proxy;
            _credentials = credentials;
            _systemProxy = systemProxy;
        }

        public long Generation => _generation.Number;
        public Uri? ProxyUri { get; }
        public CancellationToken CancellationToken => _generation.Lifetime.Token;
        internal bool HasBorrowedTransport => _generation.Connect is not null;

        public SocketsHttpHandler CreateHandler(bool allowAutoRedirect)
            => CreateHandlerCore(allowAutoRedirect, DecompressionMethods.None, null, null, false);

        public SocketsHttpHandler CreateHandler(
            bool allowAutoRedirect,
            Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connectCallback)
        {
            ArgumentNullException.ThrowIfNull(connectCallback);
            return CreateHandlerCore(allowAutoRedirect, DecompressionMethods.None, connectCallback, null, false);
        }

        internal void ThrowIfRetired() => _generation.Lifetime.ThrowIfRetired();

        internal void Register(IDisposable resource) => _generation.Lifetime.Register(resource);

        internal void Unregister(IDisposable resource) => _generation.Lifetime.Unregister(resource);

        internal SocketsHttpHandler CreatePooledHandler(
            bool allowAutoRedirect, DecompressionMethods decompression,
            OutboundResourceLifetime connections, CookieContainer cookies)
            => CreateHandlerCore(allowAutoRedirect, decompression, null, connections, true, cookies);

        private SocketsHttpHandler CreateHandlerCore(
            bool allowAutoRedirect,
            DecompressionMethods decompression,
            Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connector,
            OutboundResourceLifetime? connections,
            bool routeRedirects,
            CookieContainer? cookies = null)
        {
            ThrowIfRetired();
            var lifetime = _generation.Lifetime;
            var generation = _generation;
            var systemProxy = _systemProxy;
            var proxyEndpoint = ProxyUri;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = allowAutoRedirect,
                AutomaticDecompression = decompression,
                UseProxy = routeRedirects || ProxyUri is not null,
                Proxy = routeRedirects
                    ? new CapturedProxy(_generation, _systemProxy, ProxyUri, _credentials)
                    : ProxyUri is { } endpoint ? new CapturedProxy(endpoint, _credentials) : null,
                ConnectCallback = (context, token) => OutboundConnectionStream.ConnectAsync(
                    context, token, generation, connections, connector,
                    routeRedirects
                        ? ProxyForConnection(generation.Configuration, systemProxy, context)
                        : proxyEndpoint),
            };
            if (cookies is not null)
                handler.CookieContainer = cookies;
            if (connections is null)
                lifetime.Register(handler);
            else
            {
                try
                {
                    lifetime.ThrowIfRetired();
                    connections.Register(handler);
                }
                catch
                {
                    handler.Dispose();
                    throw;
                }
            }
            return handler;
        }
    }

    private sealed class CapturedProxy : IWebProxy, ICredentials
    {
        private readonly Route? _route;
        private readonly IWebProxy? _systemProxy;
        private readonly Uri? _endpoint;
        private readonly CredentialHolder? _credentials;
        private readonly OutboundResourceLifetime? _lifetime;
        private readonly Dictionary<Uri, CredentialHolder?> _systemCredentials = [];

        internal CapturedProxy(Uri endpoint, CredentialHolder? credentials)
        {
            _endpoint = endpoint;
            _credentials = credentials;
        }

        internal CapturedProxy(RouteGeneration generation, IWebProxy? systemProxy, Uri? endpoint, CredentialHolder? credentials)
        {
            _route = generation.Configuration;
            _lifetime = generation.Lifetime;
            _systemProxy = systemProxy;
            _credentials = credentials;
            if (systemProxy is not null && endpoint is not null)
                _systemCredentials.Add(endpoint, credentials);
        }

        public ICredentials? Credentials
        {
            get => _systemProxy is null ? _credentials : this;
            set => throw new NotSupportedException("Captured proxy credentials are immutable.");
        }

        public Uri? GetProxy(Uri destination)
        {
            var endpoint = _route is null ? _endpoint : EffectiveProxy(_route, _systemProxy, destination);
            if (_systemProxy is not null && endpoint is not null)
            {
                // Redirects can select another system proxy.
                // Capture that endpoint's credentials separately rather than sending the first proxy's secrets.
                lock (_systemCredentials)
                {
                    if (!_systemCredentials.ContainsKey(endpoint))
                    {
                        CredentialHolder? credentials = null;
                        if (_systemProxy.Credentials is { } source)
                        {
                            credentials = CredentialHolder.Capture(source, endpoint);
                            _lifetime!.Register(credentials);
                        }
                        _systemCredentials.Add(endpoint, credentials);
                    }
                }
            }
            return endpoint;
        }

        public bool IsBypassed(Uri host) => GetProxy(host) is null;

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            lock (_systemCredentials)
                return _systemCredentials.TryGetValue(uri, out var credentials)
                    ? credentials?.GetCredential(uri, authType) : null;
        }
    }

    internal sealed class CredentialHolder : ICredentials, IDisposable
    {
        private static readonly string[] AuthenticationTypes = ["Basic", "Digest", "NTLM", "Negotiate", "Kerberos"];
        private NetworkCredential? _credential;
        private Dictionary<string, NetworkCredential>? _systemCredentials;
        private readonly Uri? _systemEndpoint;

        internal CredentialHolder(NetworkCredential credential) => _credential = credential;
        internal CredentialHolder CopyForGeneration()
            => new(Copy(_credential ?? throw new ObjectDisposedException(nameof(CredentialHolder))));


        private CredentialHolder(Uri endpoint, Dictionary<string, NetworkCredential> credentials)
        {
            _systemEndpoint = endpoint;
            _systemCredentials = credentials;
        }

        internal static CredentialHolder Capture(ICredentials source, Uri endpoint)
        {
            if (source is NetworkCredential networkCredential)
                return new CredentialHolder(Copy(networkCredential));
            var credentials = new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase);
            if (endpoint.Scheme is "socks4" or "socks4a" or "socks5")
            {
                if (source.GetCredential(endpoint, endpoint.Scheme) is { } credential)
                    credentials.Add(endpoint.Scheme, Copy(credential));
            }
            else
                foreach (var type in AuthenticationTypes)
                    if (source.GetCredential(endpoint, type) is { } credential)
                        credentials.Add(type, Copy(credential));
            return new CredentialHolder(endpoint, credentials);
        }

        internal static bool Equivalent(CredentialHolder? left, CredentialHolder? right)
        {
            var first = left?._credential;
            var second = right?._credential;
            return first is null ? second is null
                : second is not null && first.UserName == second.UserName
                    && first.Password == second.Password && first.Domain == second.Domain;
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (Volatile.Read(ref _credential) is { } credential)
                return Copy(credential);
            return uri == _systemEndpoint
                && Volatile.Read(ref _systemCredentials) is { } credentials
                && credentials.TryGetValue(authType, out var systemCredential) ? Copy(systemCredential) : null;
        }

        private static NetworkCredential Copy(NetworkCredential credential)
            => ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials)
                ? credential : new NetworkCredential(credential.UserName, credential.Password, credential.Domain);

        public void Dispose()
        {
            Interlocked.Exchange(ref _credential, null);
            Interlocked.Exchange(ref _systemCredentials, null);
        }
    }

    /** Removes URL user-info, authentication headers and known raw or encoded credentials from diagnostics. */
    public static string Redact(string text, params ReadOnlySpan<string?> secrets)
    {
        text = RedactAuthorizationHeaders(text);
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret))
                continue;
            text = RedactSecret(text, secret);
            if (!secret.Contains('@'))
                continue;
            foreach (Match match in UserInfoPattern().Matches(secret))
            {
                text = RedactUserInfoSecrets(text, match.Value[..^1]);
            }
        }
        return UserInfoPattern().Replace(text, RedactedUserInfo);
    }

    /// <summary>Redacts credentials in configured URL contexts while retaining useful endpoint diagnostics.</summary>
    public static string RedactUrlCredentials(string text, params ReadOnlySpan<string?> urls)
    {
        text = RedactAuthorizationHeaders(text);
        foreach (var url in urls)
        {
            if (string.IsNullOrEmpty(url))
                continue;
            var address = url.Contains("://", StringComparison.Ordinal) ? url : $"{DefaultScheme}://{url}";
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                text = Redact(text, url);
                continue;
            }
            if (uri.Query.Length != 0 || uri.Fragment.Length != 0)
            {
                text = RedactSecret(text, url);
                text = RedactSecret(text, uri.AbsoluteUri);
            }
            text = RedactUserInfoSecrets(text, uri.UserInfo);
        }
        return UserInfoPattern().Replace(text, RedactedUserInfo);
    }

    /** Editable secret fields show a redacted value: keep the saved secret unless the user changed the text. */
    public static string? ResolveEdited(string displayed, string? saved)
        => saved is { Length: > 0 } && displayed == RedactUrlCredentials(saved, saved)
            ? saved
            : string.IsNullOrWhiteSpace(displayed) ? null : displayed.Trim();

    private static string RedactUserInfoSecrets(string text, string userInfo)
    {
        var separator = userInfo.IndexOf(':');
        if (separator < 0)
            return RedactSecret(RedactSecret(text, userInfo), Uri.UnescapeDataString(userInfo));
        var username = userInfo[..separator];
        var password = userInfo[(separator + 1)..];
        text = RedactSecret(RedactSecret(text, username), Uri.UnescapeDataString(username));
        return RedactSecret(RedactSecret(text, password), Uri.UnescapeDataString(password));
    }

    private static string RedactSecret(string text, string secret)
    {
        if (secret.Length == 0)
            return text;
        if (secret.Contains('\''))
            text = text.Replace(secret.Replace("'", "'\\''", StringComparison.Ordinal), "***", StringComparison.Ordinal);
        var uriEncoded = Uri.EscapeDataString(secret);
        if (!string.Equals(uriEncoded, secret, StringComparison.Ordinal))
            text = text.Replace(uriEncoded, "***", StringComparison.OrdinalIgnoreCase);
        var encoded = EncodeJsonSecret(secret, JavaScriptEncoder.Default);
        if (!string.Equals(encoded, secret, StringComparison.Ordinal))
            text = RedactJsonSecret(text, encoded);
        var relaxed = EncodeJsonSecret(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        if (!string.Equals(relaxed, secret, StringComparison.Ordinal) && !string.Equals(relaxed, encoded, StringComparison.Ordinal))
            text = RedactJsonSecret(text, relaxed);
        return text.Replace(secret, "***", StringComparison.Ordinal);
    }

    private static string RedactJsonSecret(string text, string encoded)
    {
        var nested = EncodeJsonSecret(encoded, JavaScriptEncoder.Default);
        if (!string.Equals(nested, encoded, StringComparison.Ordinal))
            text = text.Replace(nested, "***", StringComparison.Ordinal);
        var relaxed = EncodeJsonSecret(encoded, JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        if (!string.Equals(relaxed, nested, StringComparison.Ordinal) && !string.Equals(relaxed, encoded, StringComparison.Ordinal))
            text = text.Replace(relaxed, "***", StringComparison.Ordinal);
        return text.Replace(encoded, "***", StringComparison.Ordinal);
    }

    private static string EncodeJsonSecret(string secret, JavaScriptEncoder encoder)
    {
        foreach (var character in secret)
            if (encoder.WillEncode(character))
                return JsonEncodedText.Encode(secret, encoder).ToString();
        return secret;
    }

    private static string RedactAuthorizationHeaders(string text)
    {
        var match = AuthorizationHeaderPattern().Match(text);
        if (!match.Success)
            return text;
        var result = new StringBuilder(text.Length);
        var copied = 0;
        do
        {
            var valueStart = match.Index + match.Length;
            result.Append(text.AsSpan(copied, valueStart - copied));
            var quoteIndex = valueStart;
            while (quoteIndex < text.Length && text[quoteIndex] == '\\')
                quoteIndex++;
            var end = text.IndexOfAny(['\r', '\n'], valueStart);
            if (end < 0)
                end = text.Length;
            var escapeDepth = quoteIndex - valueStart;
            var quoteWidth = quoteIndex < end && text[quoteIndex] is '"' or '\'' ? 1
                : escapeDepth > 0 && quoteIndex + 5 <= end
                    && (text.AsSpan(quoteIndex, 5).Equals("u0022", StringComparison.OrdinalIgnoreCase)
                        || text.AsSpan(quoteIndex, 5).Equals("u0027", StringComparison.OrdinalIgnoreCase)) ? 5 : 0;
            if (quoteWidth != 0)
            {
                var delimiterWidth = escapeDepth + quoteWidth;
                result.Append(text.AsSpan(valueStart, delimiterWidth));
                result.Append("***");
                for (var index = quoteIndex + quoteWidth; index + quoteWidth <= end; index++)
                {
                    if (!text.AsSpan(index, quoteWidth).Equals(text.AsSpan(quoteIndex, quoteWidth), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var slashes = 0;
                    for (var previous = index - 1; previous > quoteIndex && text[previous] == '\\'; previous--)
                        slashes++;
                    // JSON nesting doubles backslash runs, so only the opening escape depth closes the value.
                    var escapePeriod = 2 * (escapeDepth + (quoteWidth == 1 ? 1 : 0));
                    if (slashes % escapePeriod != escapeDepth)
                        continue;
                    result.Append(text.AsSpan(index - escapeDepth, delimiterWidth));
                    end = index + quoteWidth;
                    break;
                }
            }
            else
                result.Append("***");
            copied = end;
            match = AuthorizationHeaderPattern().Match(text, copied);
        } while (match.Success);
        result.Append(text.AsSpan(copied));
        return result.ToString();
    }

    public ICredentials? Credentials
    {
        get => this;
        set => throw new NotSupportedException("outbound proxy credentials come from network.outbound_proxy.url");
    }

    public Uri? GetProxy(Uri destination)
    {
        var generation = Volatile.Read(ref _route);
        generation.ThrowIfUnavailable();
        var route = generation.Configuration;
        if (route.Address is null)
            return HttpClient.DefaultProxy.GetProxy(destination);
        return Bypassed(route, destination) ? null : route.Address;
    }

    public bool IsBypassed(Uri host)
    {
        var generation = Volatile.Read(ref _route);
        generation.ThrowIfUnavailable();
        var route = generation.Configuration;
        return route.Address is null ? HttpClient.DefaultProxy.IsBypassed(host) : Bypassed(route, host);
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var generation = Volatile.Read(ref _route);
        generation.ThrowIfUnavailable();
        var route = generation.Configuration;
        return route.Address is null ? HttpClient.DefaultProxy.Credentials?.GetCredential(uri, authType) : route.Credential?.GetCredential(uri, authType);
    }

    private static Route Parse(string url, IReadOnlyList<string> bypass)
    {
        var text = url.Contains("://", StringComparison.Ordinal) ? url : $"{DefaultScheme}://{url}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port < 0)
            throw new ArgumentException($"本机出口代理地址无效(需要 协议://主机:端口): {Redact(url)}");
        if (!SupportedSchemes.Contains(uri.Scheme))
            throw new ArgumentException($"本机出口代理不支持 {uri.Scheme} 协议(支持 http/https/socks4/socks4a/socks5): {Redact(url)}");
        var address = new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        var normalized = bypass.Select(NormalizeBypass).Where(entry => entry.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Contains(MatchAll))
            normalized = [MatchAll];
        else
            normalized = normalized.Where(entry => !normalized.Any(other =>
                entry.Length > other.Length && entry.EndsWith(other, StringComparison.Ordinal)
                    && entry[entry.Length - other.Length - 1] == '.')).ToArray();
        return new Route(address, CredentialOf(uri.UserInfo), normalized);
    }

    private static CredentialHolder? CredentialOf(string userInfo)
    {
        if (userInfo.Length == 0)
            return null;
        var colon = userInfo.IndexOf(':');
        return new CredentialHolder(colon < 0
            ? new NetworkCredential(Uri.UnescapeDataString(userInfo), "")
            : new NetworkCredential(Uri.UnescapeDataString(userInfo[..colon]), Uri.UnescapeDataString(userInfo[(colon + 1)..])));
    }

    /** "example.com"、".example.com"、"*.example.com" 都表示该域名及其子域; 单独的 "*" 表示全部直连。 */
    private static string NormalizeBypass(string entry)
    {
        var trimmed = entry.Trim().ToLowerInvariant();
        return trimmed == MatchAll ? trimmed : trimmed.TrimStart('*').TrimStart('.');
    }

    private static bool Bypassed(Route route, Uri destination)
    {
        var host = destination.IdnHost.Trim('[', ']').ToLowerInvariant();
        var local = IPAddress.TryParse(host, out var address)
            ? LocalNetworks.Any(network => network.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
            : !host.Contains('.');
        if (destination.IsLoopback || local)
            return true;
        return route.Bypass.Any(entry => entry == MatchAll || host == entry || host.EndsWith($".{entry}", StringComparison.Ordinal));
    }

    [GeneratedRegex("""(?:(?<=^|://)[^/@\s]*@|(?<![\w/])[^\s/@"':]+:[^\s/@"']+@)""")]
    private static partial Regex UserInfoPattern();

    [GeneratedRegex("""(?im)(?:\\+u002[27]|\\*["']|\b)(?:Proxy-)?Authorization(?:\\*["']|\\+u002[27])?\s*[:=][ \t]*""")]
    private static partial Regex AuthorizationHeaderPattern();
}
