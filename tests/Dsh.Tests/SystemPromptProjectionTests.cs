using Dsh.Core;
using Dsh.Llm;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed class SystemPromptProjectionTests
{
    private static Session NewSession() => Session.Create(SessionId.Create($"session-projection-{Guid.NewGuid():N}"));

    private static SessionEvent AppendSystem(Session session, string text)
        => session.Append(
            new SystemMessagePayload(0, 0, MessageFactory.CreateSystemMessage(
                text.Length == 0 ? [] : [new TextBlock(text)],
                new PluginMessageSource(SystemPromptProjection.Source))),
            new SurfaceOp.Append());

    [Fact]
    public void AppendsHeadNodeWhenNoSystemNodeExists()
    {
        var session = NewSession();
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v1");

        var commit = Assert.Single(commits);
        Assert.IsType<SurfaceOp.Append>(commit.Op);
        Assert.Equal("prompt v1", Assert.IsType<TextBlock>(commit.Message.Content[0]).Text);
        Assert.Equal(MessageRole.System, commit.Message.Role);
        Assert.IsType<PluginMessageSource>(commit.Message.Source);
    }

    [Fact]
    public void AppendsEmptyHeadNodeWhenPromptIsEmpty()
    {
        var session = NewSession();
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("");

        var commit = Assert.Single(commits);
        Assert.IsType<SurfaceOp.Append>(commit.Op);
        Assert.Empty(commit.Message.Content);
    }

    [Fact]
    public void RewritesHeadOnChange()
    {
        var session = NewSession();
        var head = AppendSystem(session, "prompt v1");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v2");

        var commit = Assert.Single(commits);
        var replace = Assert.IsType<SurfaceOp.Replace>(commit.Op);
        Assert.Equal(head.Seq, replace.Start);
        Assert.Equal(head.Seq, replace.End);
        Assert.Equal([head.Seq], commit.SourceSeqs);
        Assert.Equal("prompt v2", Assert.IsType<TextBlock>(commit.Message.Content[0]).Text);
    }

    [Fact]
    public void IsNoOpWhenUnchanged()
    {
        var session = NewSession();
        AppendSystem(session, "prompt v1");
        var projection = new SystemPromptProjection(session);

        Assert.Empty(projection.Project("prompt v1"));
    }

    [Fact]
    public void ClearsNonHeadNodesAndRewritesHead()
    {
        var session = NewSession();
        var head = AppendSystem(session, "prompt v1");
        var tail = AppendSystem(session, "prompt v2");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v3");

        Assert.Equal(2, commits.Count);
        var clearTail = commits[0];
        Assert.Equal(new SurfaceOp.Replace(tail.Seq, tail.Seq), clearTail.Op);
        Assert.Empty(clearTail.Message.Content);
        var rewriteHead = commits[1];
        Assert.Equal(new SurfaceOp.Replace(head.Seq, head.Seq), rewriteHead.Op);
        Assert.Equal("prompt v3", Assert.IsType<TextBlock>(rewriteHead.Message.Content[0]).Text);
    }

    /** 回落到旧状态(尾节点还在且内容相同)时只清掉多余节点, 不追加新 system message。 */
    [Fact]
    public void UnchangedHeadOnlyClearsStaleNonHeadNode()
    {
        var session = NewSession();
        AppendSystem(session, "prompt v1");
        var tail = AppendSystem(session, "prompt v2");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v1");

        var commit = Assert.Single(commits);
        Assert.Equal(new SurfaceOp.Replace(tail.Seq, tail.Seq), commit.Op);
        Assert.Empty(commit.Message.Content);
    }

    [Fact]
    public void InHistoryAppendsChangedPrompt()
    {
        var session = NewSession();
        AppendSystem(session, "prompt v1");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v2", inHistory: true);

        var commit = Assert.Single(commits);
        Assert.IsType<SurfaceOp.Append>(commit.Op);
        Assert.Null(commit.SourceSeqs);
        Assert.Equal("prompt v2", Assert.IsType<TextBlock>(commit.Message.Content[0]).Text);
    }

    [Fact]
    public void InHistoryIsNoOpWhenUnchanged()
    {
        var session = NewSession();
        AppendSystem(session, "prompt v1");
        var projection = new SystemPromptProjection(session);

        Assert.Empty(projection.Project("prompt v1", inHistory: true));
    }

    [Fact]
    public void InHistoryConsolidatesAtSeriesStart()
    {
        var session = NewSession();
        var head = AppendSystem(session, "prompt v1");
        AppendSystem(session, "prompt v2");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("prompt v3", inHistory: true, startsSeries: true);

        Assert.Equal(2, commits.Count);
        Assert.Equal(new SurfaceOp.Replace(head.Seq, head.Seq), commits[^1].Op);
        Assert.Equal("prompt v3", Assert.IsType<TextBlock>(commits[^1].Message.Content[0]).Text);
    }

    [Fact]
    public void InHistoryClearsAllNodesWhenPromptIsEmpty()
    {
        var session = NewSession();
        var head = AppendSystem(session, "prompt v1");
        var tail = AppendSystem(session, "prompt v2");
        var projection = new SystemPromptProjection(session);

        var commits = projection.Project("", inHistory: true);

        Assert.Equal(2, commits.Count);
        Assert.All(commits, commit => Assert.Empty(commit.Message.Content));
        Assert.Contains(commits, commit => commit.Op is SurfaceOp.Replace replace
            && replace.Start == head.Seq);
        Assert.Contains(commits, commit => commit.Op is SurfaceOp.Replace replace
            && replace.Start == tail.Seq);
    }
}
