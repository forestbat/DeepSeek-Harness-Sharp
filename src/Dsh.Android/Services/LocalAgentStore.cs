using System.Text.Json;
using Android.Content;

namespace Dsh.Android.Services;

/** 一套模型配置: 供应商 + 模型 + 可选接口地址; 密钥单独加密后按 profile 名存放。 */
public sealed record LocalModelProfile(string Name, string Provider, string Model, string BaseUrl, string ApiKey)
{
    public string Route => $"{Provider}/{Model}";

    public bool IsComplete => Provider.Trim().Length > 0 && Model.Trim().Length > 0;
}

/** 本机 agent 的模型配置集合: 多条 profile 加一个"当前"选择。 */
public sealed record LocalAgentSettings(IReadOnlyList<LocalModelProfile> Profiles, string ActiveName)
{
    public static LocalAgentSettings Empty { get; } = new([], "");

    public LocalModelProfile? Active => Profiles.FirstOrDefault(profile => profile.Name == ActiveName)
        ?? Profiles.FirstOrDefault();
}

/**
 * 本机 agent 的模型配置仓库: 手机自己跑 harness, 因此密钥也留在手机上(经 SecretBox 加密)。
 * 早期版本只能存一套 provider/model/baseUrl/apiKey, 读到旧键时迁移成一条名为"默认"的 profile。
 */
public sealed class LocalAgentStore
{
    private const string PreferencesName = "dsh-mobile";
    private const string ProfilesKey = "localAgent.profiles";
    private const string ActiveKey = "localAgent.active";
    private const string LegacyProviderKey = "localAgent.provider";
    private const string LegacyModelKey = "localAgent.model";
    private const string LegacyBaseUrlKey = "localAgent.baseUrl";
    private const string LegacyApiKeyKey = "localAgent.apiKey";
    private const string LegacyName = "默认";
    private const string DefaultProvider = "deepseek-official";
    private const string DefaultModel = "deepseek-v4-flash";

    public LocalAgentSettings Load()
    {
        var preferences = Open();
        var stored = preferences?.GetString(ProfilesKey, "") ?? "";
        if (stored.Length > 0)
        {
            var profiles = Deserialize(stored);
            if (profiles.Count > 0)
                return new LocalAgentSettings(profiles, preferences?.GetString(ActiveKey, "") ?? "");
        }
        return MigrateLegacy(preferences);
    }

    public void Save(LocalAgentSettings settings)
    {
        var preferences = Open();
        if (preferences is null)
            return;
        using var editor = preferences.Edit();
        editor?.PutString(ProfilesKey, Serialize(settings.Profiles));
        editor?.PutString(ActiveKey, settings.ActiveName);
        // 旧键在保存新结构后就没有读者了, 留着只会让人以为还有第二份配置。
        editor?.Remove(LegacyProviderKey);
        editor?.Remove(LegacyModelKey);
        editor?.Remove(LegacyBaseUrlKey);
        editor?.Remove(LegacyApiKeyKey);
        editor?.Apply();
    }

    /** 没有配置时给一条可用的默认 profile, 让"启动"按钮第一次点就有东西可跑。 */
    public static LocalModelProfile DefaultProfile()
        => new(LegacyName, DefaultProvider, DefaultModel, "", "");

    private static LocalAgentSettings MigrateLegacy(ISharedPreferences? preferences)
    {
        var provider = preferences?.GetString(LegacyProviderKey, "") ?? "";
        var model = preferences?.GetString(LegacyModelKey, "") ?? "";
        if (provider.Length == 0 && model.Length == 0)
            return LocalAgentSettings.Empty;
        var profile = new LocalModelProfile(
            LegacyName,
            provider.Length > 0 ? provider : DefaultProvider,
            model.Length > 0 ? model : DefaultModel,
            preferences?.GetString(LegacyBaseUrlKey, "") ?? "",
            SecretBox.Unprotect(preferences?.GetString(LegacyApiKeyKey, "") ?? ""));
        return new LocalAgentSettings([profile], profile.Name);
    }

    private static IReadOnlyList<LocalModelProfile> Deserialize(string json)
    {
        StoredProfile[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredProfile[]>(json);
        }
        catch (JsonException)
        {
            return [];
        }
        return [.. (stored ?? [])
            .Where(profile => profile.Name is { Length: > 0 })
            .Select(profile => new LocalModelProfile(
                profile.Name!,
                profile.Provider ?? "",
                profile.Model ?? "",
                profile.BaseUrl ?? "",
                SecretBox.Unprotect(profile.ApiKey ?? "")))];
    }

    private static string Serialize(IReadOnlyList<LocalModelProfile> profiles)
        => JsonSerializer.Serialize(profiles.Select(profile => new StoredProfile
        {
            Name = profile.Name,
            Provider = profile.Provider,
            Model = profile.Model,
            BaseUrl = profile.BaseUrl,
            ApiKey = SecretBox.Protect(profile.ApiKey),
        }));

    /** Context 只在调用期间取用; 不把 Java 代理对象留在托管字段里。 */
    private static ISharedPreferences? Open()
        => global::Android.App.Application.Context?.GetSharedPreferences(PreferencesName, FileCreationMode.Private);

    private sealed class StoredProfile
    {
        public string? Name { get; init; }

        public string? Provider { get; init; }

        public string? Model { get; init; }

        public string? BaseUrl { get; init; }

        public string? ApiKey { get; init; }
    }
}