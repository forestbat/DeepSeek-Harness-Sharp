using Cordis;
using Dsh.Core;
using Dsh.Terminal;

namespace Dsh.Tests;

public class TerminalServiceTests
{
    [Fact]
    public void RegisterBackend_ListAndDispose()
    {
        var ctx = new Context();
        _ = new AgentRegistry(ctx);
        var terminals = new TerminalSessionService(ctx);
        var backend = new StubTerminalBackend("stub");
        var dispose = terminals.RegisterBackend(backend);

        Assert.Equal(["stub"], terminals.ListBackends());
        Assert.Throws<TerminalError>(() => terminals.RegisterBackend(new StubTerminalBackend("stub")));
        dispose();
        Assert.Empty(terminals.ListBackends());
    }

    [Fact]
    public async Task Spawn_PublishesAndFencesEveryOperationToExactOwner()
    {
        var ctx = new Context();
        var agents = new AgentRegistry(ctx);
        var terminals = new TerminalSessionService(ctx);
        var backend = new StubTerminalBackend("stub");
        terminals.RegisterBackend(backend);
        var owner = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        var foreign = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        owner.Register();
        foreign.Register();

        var created = await terminals.Spawn(owner, new TerminalSpawnRequest("stub", "main", Path.GetTempPath()));
        Assert.Equal("pty-1", created.SessionId.Value);
        Assert.Equal("main", created.Name);
        Assert.Equal("stub", created.Type);
        Assert.Equal(42, created.Pid);
        Assert.Equal("running", created.Status.Kind);
        Assert.Equal("stub prompt", created.Motd);
        Assert.True(terminals.HasOwnerActivity(owner));
        Assert.Single(terminals.List(owner));
        Assert.Empty(terminals.List(foreign));
        Assert.Throws<TerminalError>(() => terminals.Read(foreign, created.SessionId));
        await Assert.ThrowsAsync<TerminalError>(() => terminals.Kill(foreign, created.SessionId));
    }

    [Fact]
    public async Task Spawn_RejectsUnknownBackendDuplicateNameAndActiveSend()
    {
        var ctx = new Context();
        var agents = new AgentRegistry(ctx);
        var terminals = new TerminalSessionService(ctx);
        var backend = new StubTerminalBackend("stub");
        terminals.RegisterBackend(backend);
        var owner = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        owner.Register();

        await Assert.ThrowsAsync<TerminalError>(() => terminals.Spawn(owner, new TerminalSpawnRequest("missing")));
        var created = await terminals.Spawn(owner, new TerminalSpawnRequest("stub", "main"));
        await Assert.ThrowsAsync<TerminalError>(() => terminals.Spawn(owner, new TerminalSpawnRequest("stub", "main")));

        backend.Sessions[0].AutoSettle = false;
        var operation = terminals.StartSend(owner, created.SessionId, new TerminalSendRequest("echo hi", true));
        Assert.Throws<TerminalError>(() => terminals.StartSend(owner, created.SessionId, new TerminalSendRequest("pwd", true)));
        operation.Cancel();
        await operation.Done;
    }

    [Fact]
    public async Task Kill_JoinsAlreadyClosing()
    {
        var ctx = new Context();
        var agents = new AgentRegistry(ctx);
        var terminals = new TerminalSessionService(ctx);
        var backend = new StubTerminalBackend("stub");
        terminals.RegisterBackend(backend);
        var owner = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        owner.Register();
        var created = await terminals.Spawn(owner, new TerminalSpawnRequest("stub"));
        var session = backend.Sessions[0];
        session.CloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = terminals.Kill(owner, created.SessionId);
        var second = terminals.Kill(owner, created.SessionId);
        Assert.Throws<InvalidOperationException>(() => terminals.StartSend(owner, created.SessionId, new TerminalSendRequest("", false)));
        session.CloseGate.SetResult();
        Assert.True(await first);
        Assert.False(await second);
        Assert.Throws<TerminalError>(() => terminals.Read(owner, created.SessionId));
    }

    [Fact]
    public async Task HasOwnerActivity_CoversPendingSpawn()
    {
        var ctx = new Context();
        var agents = new AgentRegistry(ctx);
        var terminals = new TerminalSessionService(ctx);
        var gate = new TaskCompletionSource<TerminalBackendSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminals.RegisterBackend(new StubTerminalBackend("slow", () => gate.Task));
        var owner = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        owner.Register();

        var pending = terminals.Spawn(owner, new TerminalSpawnRequest("slow"));
        Assert.True(terminals.HasOwnerActivity(owner));
        gate.SetResult(new StubTerminalSession());
        await pending;
        Assert.True(terminals.HasOwnerActivity(owner));
        await terminals.Kill(owner, new TerminalSessionId("pty-1"));
        Assert.False(terminals.HasOwnerActivity(owner));
    }
}
