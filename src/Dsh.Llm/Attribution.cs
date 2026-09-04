using System.Reflection;

namespace Dsh.Llm;

public sealed record AppIdentity(string Product, string Version, string Url)
{
    public static readonly AppIdentity Default = new(
        "deepseek-harness",
        typeof(AppIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0",
        "https://github.com/deepseek-ai/deepseek-harness");

    public string UserAgent => $"{Product}/{Version} (+{Url})";
}

public static class ApiKey
{
    public static bool Normalize(string raw, out string value, out string? rejection)
    {
        value = raw.Trim();
        if (value.Length == 0)
        {
            rejection = "empty";
            return false;
        }
        foreach (var ch in value)
        {
            if (ch is < '\x21' or > '\x7E')
            {
                rejection = "illegalCharacters";
                return false;
            }
        }
        rejection = null;
        return true;
    }
}
