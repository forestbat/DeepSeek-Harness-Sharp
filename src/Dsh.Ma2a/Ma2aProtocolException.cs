namespace Dsh.Ma2a;

public sealed class Ma2aProtocolException(string error, string? remediation = null)
    : IOException(remediation is null ? error : $"{error}: {remediation}")
{
    public string Error { get; } = error;
    public string? Remediation { get; } = remediation;
}
