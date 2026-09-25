using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Account;

/** 凭据保护 seam: Windows 用 DPAPI, Unix 用文件权限, 失败时由存储层 fail-closed。 */
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedBytes);
}

public static class SecretProtector
{
    public static ISecretProtector Create()
        => OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new PassThroughSecretProtector();

    [SupportedOSPlatform("windows")]
    private sealed class DpapiSecretProtector : ISecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dsh.Account.v1");

        public byte[] Protect(byte[] plaintext)
            => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        public byte[] Unprotect(byte[] protectedBytes)
            => ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes;
    }
}
