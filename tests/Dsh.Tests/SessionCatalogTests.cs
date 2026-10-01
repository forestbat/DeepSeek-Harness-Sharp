using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Llm;

namespace Dsh.Tests;

/** GUI 会话目录: 子代理会话不出现在会话列表里; "从检查点恢复"的会话(有 ParentSession 但非 subagent)必须保留。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class SessionCatalogTests
{
    [Fact]
    public void IsSubagent_Only_Matches_Subagent_Origin()
    {
        Assert.True(Header(origin: "subagent").IsSubagent);
        Assert.False(Header(origin: null, parent: SessionId.Create("session-parent")).IsSubagent);
        Assert.False(Header().IsSubagent);
    }

    [Fact]
    public async Task Subagent_Sessions_Are_Hidden_From_The_Catalog()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        var parent = environment.Agent.Id;
        var subagent = SessionId.Create($"session-{Guid.NewGuid():N}");
        var restored = SessionId.Create($"session-{Guid.NewGuid():N}");
        persistence.Create(Header(id: subagent, origin: "subagent", parent: parent, depth: 1));

        // "从检查点恢复"会话: 有 ParentSession 但不是子代理, 必须照常出现在列表里。
        persistence.Create(Header(id: restored, origin: null, parent: parent));

        var nodes = new SessionCatalog(environment.App.Ctx).Load();

        Assert.DoesNotContain(nodes, node => node.SessionId == subagent);
        Assert.Contains(nodes, node => node.SessionId == restored);
        Assert.Contains(nodes, node => node.SessionId == parent);
    }

    private static SessionHeader Header(
        SessionId? id = null,
        string? origin = null,
        SessionId? parent = null,
        int? depth = null)
    {
        var headerId = id ?? SessionId.Create($"session-{Guid.NewGuid():N}");
        var isSubagent = string.Equals(origin, "subagent", StringComparison.Ordinal);
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = headerId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsSeeded = false,
            ParentSession = parent,
            Origin = origin,
            DelegationDepth = depth,
            RootSession = isSubagent ? parent : null,
            SubagentProvider = isSubagent ? "spawn" : null,
            SubagentMode = isSubagent ? "one-shot" : null,
            SubagentLabel = isSubagent ? "test" : null,
        };
    }
}
