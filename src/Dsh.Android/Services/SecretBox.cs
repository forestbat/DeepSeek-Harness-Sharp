using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Dsh.Android.Services;

/**
 * 访问令牌的静态加密: 密钥放在 Android Keystore(不可导出), 密文带 "v1:" 前缀。
 * 没有前缀的旧值按明文读入, 下次保存时迁移; 解密失败(密钥失效)返回空串, 让用户重新输入。
 */
public static class SecretBox
{
    private const string KeyAlias = "dsh-access-token";
    private const string KeyStoreName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const string Prefix = "v1:";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    public static string Protect(string value)
        => value.Length == 0 ? "" : Prefix + Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(value)));

    public static string Unprotect(string stored)
    {
        if (stored.Length == 0)
            return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        try
        {
            return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(stored[Prefix.Length..])));
        }
        catch (Exception error) when (error is GeneralSecurityException or Java.Lang.Exception or FormatException)
        {
            return "";
        }
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var cipher = Cipher.GetInstance(Transformation) ?? throw new InvalidOperationException("AES-GCM unavailable");
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("AES-GCM returned no IV");
        var encrypted = cipher.DoFinal(plain) ?? throw new InvalidOperationException("encrypt failed");
        var payload = new byte[iv.Length + encrypted.Length];
        iv.CopyTo(payload, 0);
        encrypted.CopyTo(payload, iv.Length);
        return payload;
    }

    private static byte[] Decrypt(byte[] payload)
    {
        if (payload.Length <= IvBytes)
            throw new FormatException("ciphertext too short");
        using var cipher = Cipher.GetInstance(Transformation) ?? throw new InvalidOperationException("AES-GCM unavailable");
        cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(TagBits, payload[..IvBytes]));
        return cipher.DoFinal(payload[IvBytes..]) ?? throw new InvalidOperationException("decrypt failed");
    }

    private static IKey Key()
    {
        using var store = KeyStore.GetInstance(KeyStoreName) ?? throw new InvalidOperationException("AndroidKeyStore unavailable");
        store.Load((KeyStore.ILoadStoreParameter?)null);
        if (store.GetKey(KeyAlias, null) is { } existing)
            return existing;
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)
            ?? throw new InvalidOperationException("AES key generator unavailable");
        using var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(spec);
        generator.GenerateKey();
        return store.GetKey(KeyAlias, null) ?? throw new InvalidOperationException("key generation failed");
    }
}
