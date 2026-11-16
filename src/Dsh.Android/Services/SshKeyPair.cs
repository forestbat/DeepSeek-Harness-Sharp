using System.Security.Cryptography;
using System.Text;

namespace Dsh.Android.Services;

/**
 * 手机端自带的 SSH 身份: RSA 3072 私钥用 PKCS#1 PEM 保存(再由 SecretBox 加密落盘),
 * 公钥按 OpenSSH 单行格式展示, 用户把它加进桌面机的 authorized_keys。
 */
public static class SshKeyPair
{
    private const int KeyBits = 3072;
    private const string Comment = "dsh-android";

    public static string GeneratePrivateKeyPem()
    {
        using var rsa = RSA.Create(KeyBits);
        return rsa.ExportRSAPrivateKeyPem();
    }

    /** 从私钥 PEM 推导 OpenSSH 公钥行; PEM 损坏时返回 null。 */
    public static string? PublicKeyLine(string privateKeyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var parameters = rsa.ExportParameters(includePrivateParameters: false);
            if (parameters.Modulus is null || parameters.Exponent is null)
                return null;
            using var blob = new MemoryStream();
            WriteString(blob, "ssh-rsa");
            WriteMpint(blob, parameters.Exponent);
            WriteMpint(blob, parameters.Modulus);
            return $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} {Comment}";
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    /** SSH mpint: 大端长度 + 内容; 最高位为 1 时补一个 0x00, 避免被当作负数。 */
    private static void WriteMpint(Stream stream, byte[] value)
    {
        var start = 0;
        while (start < value.Length - 1 && value[start] == 0)
            start++;
        var length = value.Length - start;
        var pad = (value[start] & 0x80) != 0 ? 1 : 0;
        WriteUInt32(stream, (uint)(length + pad));
        if (pad == 1)
            stream.WriteByte(0);
        stream.Write(value, start, length);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
