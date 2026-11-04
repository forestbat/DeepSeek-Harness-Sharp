using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Content;

namespace Dsh.Android.Services;

public enum HostTransportKind
{
    Tcp,
    Ssh,
}

/** 一台桌面 host 的连接配置; SSH 走 App 内生成的密钥, 令牌与私钥都由 SecretBox 加密落盘。 */
public sealed record HostProfile(
    string Name,
    HostTransportKind Transport,
    string Host,
    int Port,
    string Token,
    string SshUser,
    string SshFingerprint)
{
    public bool IsComplete => Name.Trim().Length > 0
        && Host.Trim().Length > 0
        && Port is > 0 and < 65536
        && (Transport == HostTransportKind.Tcp || SshUser.Trim().Length > 0);

    public string Display => Transport == HostTransportKind.Ssh
        ? $"ssh://{SshUser.Trim()}@{Host.Trim()}:{Port}"
        : $"{Host.Trim()}:{Port}";
}

/**
 * 多台桌面机的配置仓库: 每台机器的令牌各自加密, 机器之间互不影响。
 * 早期版本只存一台机器(host/port/token 三个键), 这里在读到旧键时迁移成一台名为"桌面机"的记录。
 */
public sealed class MachineStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string MachinesKey = "machines";
    private const string SshKeyKey = "sshPrivateKey";
    private const string LegacyHostKey = "host";
    private const string LegacyPortKey = "port";
    private const string LegacyTokenKey = "token";
    private const string LegacyTransportKey = "transport";
    private const string LegacySshUserKey = "sshUser";
    private const string LegacySshFingerprintKey = "sshFingerprint";
    private const string LegacyMachineName = "桌面机";

    public IReadOnlyList<HostProfile> Load()
    {
        var stored = Open()?.GetString(MachinesKey, "") ?? "";
        if (stored.Length > 0)
            return Deserialize(stored);
        return MigrateLegacy();
    }

    public void Save(IReadOnlyList<HostProfile> machines)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(MachinesKey, Serialize(machines));
        editor?.Apply();
    }

    public void SaveFingerprint(string machineName, string fingerprint)
    {
        var machines = Load()
            .Select(machine => machine.Name == machineName ? machine with { SshFingerprint = fingerprint } : machine)
            .ToList();
        Save(machines);
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

    private static IReadOnlyList<HostProfile> Deserialize(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredMachine[]>(json) ?? [];
            return [.. stored
                .Where(machine => machine.Name is { Length: > 0 })
                .Select(machine => new HostProfile(
                    machine.Name!,
                    machine.Transport == "ssh" ? HostTransportKind.Ssh : HostTransportKind.Tcp,
                    machine.Host ?? "",
                    machine.Port,
                    SecretBox.Unprotect(machine.Token ?? ""),
                    machine.SshUser ?? "",
                    machine.SshFingerprint ?? ""))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Serialize(IReadOnlyList<HostProfile> machines)
        => JsonSerializer.Serialize(machines.Select(machine => new StoredMachine
        {
            Name = machine.Name,
            Transport = machine.Transport == HostTransportKind.Ssh ? "ssh" : "tcp",
            Host = machine.Host,
            Port = machine.Port,
            Token = SecretBox.Protect(machine.Token),
            SshUser = machine.SshUser,
            SshFingerprint = machine.SshFingerprint,
        }).ToArray());

    private static IReadOnlyList<HostProfile> MigrateLegacy()
    {
        var preferences = Open();
        var host = preferences?.GetString(LegacyHostKey, "") ?? "";
        if (host.Length == 0)
            return [];
        var transport = preferences?.GetString(LegacyTransportKey, "tcp") == "ssh"
            ? HostTransportKind.Ssh
            : HostTransportKind.Tcp;
        var fallbackPort = transport == HostTransportKind.Ssh ? 22 : 47321;
        var port = int.TryParse(preferences?.GetString(LegacyPortKey, ""), out var parsed) ? parsed : fallbackPort;
        return
        [
            new HostProfile(
                LegacyMachineName,
                transport,
                host,
                port,
                SecretBox.Unprotect(preferences?.GetString(LegacyTokenKey, "") ?? ""),
                preferences?.GetString(LegacySshUserKey, "") ?? "",
                preferences?.GetString(LegacySshFingerprintKey, "") ?? ""),
        ];
    }

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里, 避免 Java GC 与托管 GC 互相牵挂。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);

    private sealed class StoredMachine
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("transport")]
        public string? Transport { get; set; }

        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("sshUser")]
        public string? SshUser { get; set; }

        [JsonPropertyName("sshFingerprint")]
        public string? SshFingerprint { get; set; }
    }
}
