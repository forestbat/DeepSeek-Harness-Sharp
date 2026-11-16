using System.Security.Cryptography;
using Android.Content;

namespace Dsh.Android.Services;

public sealed record PhoneToolSettings(bool Enabled, int Port, string Token);

/**
 * 手机工具(MCP)的监听配置: 端口与访问令牌。
 * 令牌在首次读取时生成并落盘, 电脑端把它拼在 URL 里即可访问, 不需要额外的账号体系。
 */
public sealed class PhoneToolStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string EnabledKey = "phoneTools.enabled";
    private const string PortKey = "phoneTools.port";
    private const string TokenKey = "phoneTools.token";
    private const int DefaultPort = 47322;
    private const int TokenBytes = 16;

    public PhoneToolSettings Load()
    {
        var preferences = Open();
        var port = preferences?.GetInt(PortKey, DefaultPort) ?? DefaultPort;
        if (port is <= 0 or > 65535)
            port = DefaultPort;
        var token = preferences?.GetString(TokenKey, "") ?? "";
        if (token.Length == 0)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
            SaveToken(token);
        }
        // 默认开启: 手机工具是这个版本的主要内容, 地址带令牌, 关掉后不会再自动打开。
        return new PhoneToolSettings(preferences?.GetBoolean(EnabledKey, true) ?? true, port, token);
    }

    public void Save(PhoneToolSettings settings)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutBoolean(EnabledKey, settings.Enabled);
        editor?.PutInt(PortKey, settings.Port);
        editor?.PutString(TokenKey, settings.Token);
        editor?.Apply();
    }

    private static void SaveToken(string token)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(TokenKey, token);
        editor?.Apply();
    }

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
