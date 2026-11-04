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

    /** 目录按 provider 名精确对 id, 不中再按 baseUrl 端点(host:port)对 api; 中转站两路都不命中就返回空(不猜)。 */
    [Fact]
    public void ModelIdsFor_Matches_ById_Then_By_Endpoint()
    {
        var catalog = ModelsDevCatalog.FromJson(Fixture);

        Assert.Equal(["glm-5.3"], catalog.ModelIdsFor("zhipuai-coding-plan", null));
        Assert.Equal(["glm-5.3"], catalog.ModelIdsFor("ZHIPUAI-CODING-PLAN", null));
        Assert.Equal(["glm-5.3"], catalog.ModelIdsFor("relay", "https://open.bigmodel.cn/api/coding/paas/v4/"));
        Assert.Equal(["glm-5.3"], catalog.ModelIdsFor("relay", "https://open.bigmodel.cn:443/api/coding/paas/v4"));
        // 同主机不同端口不是同一个端点(127.0.0.1:1 不该撞目录里的本地 runtime)。
        Assert.Empty(catalog.ModelIdsFor("my-endpoint", "http://127.0.0.1:1/v1"));
        Assert.Empty(catalog.ModelIdsFor("boomfirst", "https://www.boomfirst.cc/v1"));
        Assert.Empty(catalog.ModelIdsFor(null, null));
    }

    /** 有效模型 = 声明 ∪ 目录(声明在前去重): 目录已收录的模型不必写进 settings.yaml。 */
    [Fact]
    public void EffectiveModels_Unions_Declared_With_Catalog()
    {
        var catalog = ModelsDevCatalog.FromJson(Fixture);

        Assert.Equal(["glm-5.5-preview", "glm-5.3"],
            ProviderCatalog.EffectiveModels(catalog, "zhipuai-coding-plan", null, ["glm-5.5-preview", "glm-5.3"]));
        Assert.Equal(["glm-5.3"], ProviderCatalog.EffectiveModels(catalog, "zhipuai-coding-plan", null, []));
        Assert.Equal(["my-model"],
            ProviderCatalog.EffectiveModels(catalog, "boomfirst", "https://www.boomfirst.cc/v1", ["my-model"]));
    }

    /** 上下文窗口取 models.dev 的 limit.context: 缺失、非正数与未收录的模型都按"未知"处理。 */
    [Fact]
    public void ContextWindowFor_Reads_Limit_Context()
    {
        const string json = """
            {
              "deepseek": {
                "id": "deepseek",
                "name": "DeepSeek",
                "env": ["DEEPSEEK_API_KEY"],
                "api": "https://api.deepseek.com",
                "models": {
                  "deepseek-v4-flash": { "name": "DeepSeek V4 Flash", "limit": { "context": 1000000, "output": 393216 } },
                  "no-limit": { "name": "No Limit" },
                  "zero-limit": { "name": "Zero", "limit": { "context": 0 } }
                }
              }
            }
            """;
        var catalog = ModelsDevCatalog.FromJson(json);

        Assert.Equal(1_000_000, catalog.ContextWindowFor("deepseek", "deepseek-v4-flash"));
        Assert.Null(catalog.ContextWindowFor("deepseek", "no-limit"));
        Assert.Null(catalog.ContextWindowFor("deepseek", "zero-limit"));
        Assert.Null(catalog.ContextWindowFor("relay", "deepseek-v4-flash"));
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
