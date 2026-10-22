using Android.Content;

namespace Dsh.Android.Services;

/**
 * 手机端保存的远端地址与访问令牌。
 * 令牌与主机名存在 Android 私有的 SharedPreferences 里; 只在建立连接与保存时读写, 不在热路径上保留 Java 对象。
 */
public sealed record HostEndpoint(string Host, int Port, string Token)
{
    public bool IsComplete => Host.Length > 0 && Port is > 0 and < 65536;

    public string Display => $"{Host}:{Port}";
}

public sealed class EndpointStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string HostKey = "host";
    private const string PortKey = "port";
    private const string TokenKey = "token";

    public HostEndpoint Load()
    {
        var preferences = Open();
        if (preferences is null)
            return new HostEndpoint("", 47321, "");
        var host = preferences.GetString(HostKey, "") ?? "";
        var port = int.TryParse(preferences.GetString(PortKey, ""), out var parsed) ? parsed : 47321;
        return new HostEndpoint(host, port, preferences.GetString(TokenKey, "") ?? "");
    }

    public void Save(HostEndpoint endpoint)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        if (editor is null)
            return;
        editor.PutString(HostKey, endpoint.Host);
        editor.PutString(PortKey, endpoint.Port.ToString());
        editor.PutString(TokenKey, endpoint.Token);
        editor.Apply();
    }

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里, 避免 Java GC 与托管 GC 互相牵挂。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
