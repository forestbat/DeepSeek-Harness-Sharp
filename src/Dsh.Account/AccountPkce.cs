using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Account;

/** PKCE(S256) 与 state 生成/校验: verifier 与 state 均为 32 字节 base64url 随机。 */
public static class AccountPkce
{
    private const int VerifierBytes = 32;

    private const int StateBytes = 32;

    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(VerifierBytes));

    public static string NewState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));

    public static string Challenge(string verifier)
        => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.ASCII.GetBytes(expected);
        var right = Encoding.ASCII.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
