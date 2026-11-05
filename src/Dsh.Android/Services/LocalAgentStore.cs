using Android.Content;

namespace Dsh.Android.Services;

public sealed record LocalAgentSettings(string Provider, string Model, string BaseUrl, string ApiKey);

/** 本机 agent 的模型配置: 手机自己跑 harness, 因此密钥也留在手机上(仍由 SecretBox 加密)。 */
public sealed class LocalAgentStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string ProviderKey = "localAgent.provider";
    private const string ModelKey = "localAgent.model";
    private const string BaseUrlKey = "localAgent.baseUrl";
    private const string ApiKeyKey = "localAgent.apiKey";
    private const string DefaultProvider = "deepseek-official";
    private const string DefaultModel = "deepseek-v4-flash";

    public LocalAgentSettings Load()
    {
        var preferences = Open();
        return new LocalAgentSettings(
            preferences?.GetString(ProviderKey, DefaultProvider) ?? DefaultProvider,
            preferences?.GetString(ModelKey, DefaultModel) ?? DefaultModel,
            preferences?.GetString(BaseUrlKey, "") ?? "",
            SecretBox.Unprotect(preferences?.GetString(ApiKeyKey, "") ?? ""));
    }

    public void Save(LocalAgentSettings settings)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(ProviderKey, settings.Provider);
        editor?.PutString(ModelKey, settings.Model);
        editor?.PutString(BaseUrlKey, settings.BaseUrl);
        editor?.PutString(ApiKeyKey, SecretBox.Protect(settings.ApiKey));
        editor?.Apply();
    }

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
}
