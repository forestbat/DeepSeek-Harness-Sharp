using Dsh.Boot;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class ProviderCatalogTests
{
    internal const string Fixture = """
        {
          "zhipuai-coding-plan": {
            "id": "zhipuai-coding-plan",
            "name": "Zhipu AI Coding Plan",
            "env": ["ZHIPU_API_KEY"],
            "npm": "@ai-sdk/openai-compatible",
            "api": "https://open.bigmodel.cn/api/coding/paas/v4",
            "models": { "glm-5.3": { "name": "GLM-5.3" } }
          },
          "kimi-code-plan-global": {
            "id": "kimi-code-plan-global",
            "name": "Kimi Code Plan",
            "env": ["KIMI_API_KEY"],
            "npm": "@ai-sdk/openai-compatible",
            "api": "https://api.moonshot.ai/anthropic",
            "models": {}
          },
          "openai": {
            "id": "openai",
            "name": "OpenAI",
            "env": ["OPENAI_API_KEY"],
            "npm": "@ai-sdk/openai",
            "api": "https://api.openai.com/v1",
            "models": {}
          },
          "anthropic": {
            "id": "anthropic",
            "name": "Anthropic",
            "env": ["ANTHROPIC_API_KEY"],
            "npm": "@ai-sdk/anthropic",
            "api": "https://api.anthropic.com",
            "models": {}
          },
          "azure-openai": {
            "id": "azure-openai",
            "name": "Azure OpenAI",
            "env": ["AZURE_API_KEY"],
            "npm": "@ai-sdk/azure",
            "api": "https://example.openai.azure.com",
            "models": {}
          }
        }
        """;

    /** npm(SDK 方言) → 协议族与 API 风格; 没有适配器的 provider 标"需插件"。 */
    [Fact]
    public void Parse_Maps_Npm_Dialects_And_Flags_Unsupported()
    {
        var snapshot = ProviderCatalog.Parse(Fixture);

        Assert.Equal(5, snapshot.Providers.Count);
        Assert.Equal(["anthropic", "azure-openai", "kimi-code-plan-global", "openai", "zhipuai-coding-plan"],
            snapshot.Providers.Select(provider => provider.Id));

        var zhipu = Assert.Single(snapshot.Providers, provider => provider.Id == "zhipuai-coding-plan");
        Assert.Equal(ProviderTypes.OpenAiCompatible, zhipu.Type);
        Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", zhipu.BaseUrl);
        Assert.Equal(["ZHIPU_API_KEY"], zhipu.EnvironmentKeys);
        Assert.Equal(["glm-5.3"], zhipu.ModelIds);

        Assert.Equal(ProviderTypes.OpenAiCompatibleResponses,
            Assert.Single(snapshot.Providers, provider => provider.Id == "openai").Type);
        Assert.Equal(ProviderTypes.AnthropicMessages,
            Assert.Single(snapshot.Providers, provider => provider.Id == "anthropic").Type);

        var azure = Assert.Single(snapshot.Providers, provider => provider.Id == "azure-openai");
        Assert.Null(azure.Type);
        Assert.Contains("需插件", azure.Describe());
        Assert.Contains("type=openai-compatible, baseUrl=https://open.bigmodel.cn", zhipu.Describe());
    }

    [Fact]
    public void TypeOf_Covers_BuiltIn_Override_Anthropic_Suffix_And_Unknowns()
    {
        Assert.Equal(ProviderTypes.DeepSeek, ProviderCatalog.TypeOf("deepseek", "@ai-sdk/openai-compatible"));
        Assert.Equal(ProviderTypes.AnthropicMessages, ProviderCatalog.TypeOf("google-vertex", "@ai-sdk/google-vertex/anthropic"));
        Assert.Null(ProviderCatalog.TypeOf("google", "@ai-sdk/google"));
        Assert.Null(ProviderCatalog.TypeOf("x", null));
    }

    /** 内置快照随程序发布: 没有缓存也保证目录可用(离线), 且 DeepSeek 走专有类型。 */
    [Fact]
    public void Embedded_Snapshot_Is_Available_Without_Cache()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-catalog-embedded", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var snapshot = ProviderCatalog.LoadCached(new HarnessHome(home));

            Assert.True(snapshot.FromEmbedded);
            Assert.Equal(225, snapshot.Providers.Count);
            Assert.True(snapshot.Providers.Count(provider => provider.BaseUrl is not null) >= 190);
            Assert.Equal(ProviderTypes.DeepSeek, Assert.Single(snapshot.Providers, provider => provider.Id == "deepseek").Type);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    /** 缓存文件是菜单候选的数据源: 只保留有适配器的 provider, 且不联网也能用。 */
    [Fact]
    public void LoadCached_Reads_Cache_Without_Network()
    {
        var home = Path.Combine(Path.GetTempPath(), "dsh-provider-catalog", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = ProviderCatalog.CacheFile(HarnessHome.Resolve(home));
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            File.WriteAllText(cache, Fixture);

            var snapshot = ProviderCatalog.LoadCached(HarnessHome.Resolve(home));

            Assert.True(snapshot.FromCache);
            Assert.Null(snapshot.Error);
            Assert.Equal(5, snapshot.Providers.Count);
            Assert.NotNull(snapshot.FetchedAt);
        }
        finally
        {
            if (Directory.Exists(home))
                Directory.Delete(home, true);
        }

        var fallback = ProviderCatalog.LoadCached(HarnessHome.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.True(fallback.FromEmbedded);
        Assert.NotEmpty(fallback.Providers);
    }
}
