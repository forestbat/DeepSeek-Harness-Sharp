namespace Dsh.Llm;

public static class Endpoint
{
    public static string NormalizeBaseUrl(string? value)
    {
        var url = value?.Trim() ?? "";
        if (url.Length == 0)
            return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !Uri.TryCreate($"https://{url}", UriKind.Absolute, out uri))
        {
            throw new LlmException(new LlmFailure($"invalid base URL: {url}", "INVALID_BASE_URL"));
        }
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new LlmException(new LlmFailure($"base URL must use http or https: {url}", "INVALID_BASE_URL"));
        }
        var builder = new UriBuilder(uri);
        builder.Path = builder.Path.TrimEnd('/');
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
