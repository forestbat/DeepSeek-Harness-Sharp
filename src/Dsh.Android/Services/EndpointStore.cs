using Android.Content;

namespace Dsh.Android.Services;

public enum HostTransportKind
{
    Tcp,
    Ssh,
}

/** 一台桌面 host 的连接配置; SSH 走 App 内生成的密钥, 私钥与令牌都由 SecretBox 加密落盘。 */
public sealed record HostProfile(
    HostTransportKind Transport,
    string Host,
    int Port,
    string Token,
    string SshUser,
    string SshFingerprint)
{
    public bool IsComplete => Host.Length > 0
        && Port is > 0 and < 65536
        && (Transport == HostTransportKind.Tcp || SshUser.Length > 0);

    public string Display => Transport == HostTransportKind.Ssh
        ? $"ssh://{SshUser}@{Host}:{Port}"
        : $"{Host}:{Port}";
}

public sealed class EndpointStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string HostKey = "host";
    private const string PortKey = "port";
    private const string TokenKey = "token";
    private const string TransportKey = "transport";
    private const string SshUserKey = "sshUser";
    private const string SshFingerprintKey = "sshFingerprint";
    private const string SshKeyKey = "sshPrivateKey";

    public HostProfile Load()
    {
        var preferences = Open();
        if (preferences is null)
            return new HostProfile(HostTransportKind.Tcp, "", 47321, "", "", "");
        var ssh = preferences.GetString(TransportKey, "tcp") == "ssh";
        var port = int.TryParse(preferences.GetString(PortKey, ""), out var parsed) ? parsed : ssh ? 22 : 47321;
        return new HostProfile(
            ssh ? HostTransportKind.Ssh : HostTransportKind.Tcp,
            preferences.GetString(HostKey, "") ?? "",
            port,
            SecretBox.Unprotect(preferences.GetString(TokenKey, "") ?? ""),
            preferences.GetString(SshUserKey, "") ?? "",
            preferences.GetString(SshFingerprintKey, "") ?? "");
    }

    public void Save(HostProfile profile)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        if (editor is null)
            return;
        editor.PutString(HostKey, profile.Host);
        editor.PutString(PortKey, profile.Port.ToString());
        editor.PutString(TokenKey, SecretBox.Protect(profile.Token));
        editor.PutString(TransportKey, profile.Transport == HostTransportKind.Ssh ? "ssh" : "tcp");
        editor.PutString(SshUserKey, profile.SshUser);
        editor.PutString(SshFingerprintKey, profile.SshFingerprint);
        editor.Apply();
    }

    public void SaveFingerprint(string fingerprint)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(SshFingerprintKey, fingerprint);
        editor?.Apply();
    }

    /** 读取 App 生成的 SSH 私钥(PKCS#1 PEM); 没有则返回 null。 */
    public string? LoadPrivateKey()
    {
        var stored = Open()?.GetString(SshKeyKey, "") ?? "";
        return stored.Length == 0 ? null : SecretBox.Unprotect(stored);
    }

    public void SavePrivateKey(string pem)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(SshKeyKey, SecretBox.Protect(pem));
        editor?.Apply();
    }

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里, 避免 Java GC 与托管 GC 互相牵挂。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
