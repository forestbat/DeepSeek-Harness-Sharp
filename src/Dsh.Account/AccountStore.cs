using System.Text.Json;
using Dsh.Boot;

namespace Dsh.Account;

/** 授权记录: version=1, token 与签发它的 platformOrigin。 */
public sealed record AccountGrant(int Version, string Token, string Issuer);

/** 设备标识记录: 独立于授权, 首次使用时生成随机 UUID。 */
public sealed record AccountDevice(string Id);

/**
 * 账号本地存储: HarnessHome 下的 web-login/ 目录, grant 与 device 各一份。
 * Windows 用 DPAPI 加密整份文件; Unix 退化为 0600 权限(不伪造自研加密)。
 * 读取失败一律 fail-closed: 视为未登录, 绝不回退明文, 也不静默重建授权。
 */
public sealed class AccountStore
{
    private const string GrantFileName = "deepseek-account.json";

    private const string DeviceFileName = "deepseek-account-device.json";

    private readonly string _directory;
    private readonly string _grantPath;
    private readonly string _devicePath;
    private readonly ISecretProtector _protector;

    public AccountStore(HarnessHome home, ISecretProtector? protector = null)
    {
        _directory = Path.Combine(home.Root, "web-login");
        _grantPath = Path.Combine(_directory, GrantFileName);
        _devicePath = Path.Combine(_directory, DeviceFileName);
        _protector = protector ?? SecretProtector.Create();
    }

    public string GrantPath => _grantPath;

    public string DevicePath => _devicePath;

    public AccountGrant? ReadGrant(string expectedIssuer)
    {
        var payload = ReadProtected(_grantPath);
        if (payload is null)
            return null;
        AccountGrant? grant;
        try
        {
            grant = JsonSerializer.Deserialize(payload, DshAccountJsonContext.Default.AccountGrant);
        }
        catch (JsonException)
        {
            return null;
        }
        if (grant is null || grant.Version != 1 || string.IsNullOrEmpty(grant.Token))
            return null;
        if (!string.Equals(grant.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            DeleteGrant();
            return null;
        }
        return grant;
    }

    public void WriteGrant(AccountGrant grant) => WriteProtected(_grantPath, grant, DshAccountJsonContext.Default.AccountGrant);

    public void DeleteGrant() => TryDelete(_grantPath);

    public string GetOrCreateDeviceId()
    {
        var existing = ReadDevice();
        if (existing is not null)
            return existing.Id;
        var created = new AccountDevice(Guid.NewGuid().ToString());
        WriteProtected(_devicePath, created, DshAccountJsonContext.Default.AccountDevice);
        return created.Id;
    }

    private AccountDevice? ReadDevice()
    {
        var payload = ReadProtected(_devicePath);
        if (payload is null)
            return null;
        try
        {
            var device = JsonSerializer.Deserialize(payload, DshAccountJsonContext.Default.AccountDevice);
            return string.IsNullOrEmpty(device?.Id) ? null : device;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private byte[]? ReadProtected(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return _protector.Unprotect(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteProtected<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        EnsureDirectory();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        try
        {
            File.WriteAllBytes(path, _protector.Protect(bytes));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AccountException(AccountFailure.Storage);
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(_directory);
            else
                Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AccountException(AccountFailure.Storage);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
