using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public class SessionTitleFromUserMessageTests
{
    [Fact]
    public void Derive_Uses_First_Line_And_Collapses_Whitespace()
    {
        var message = MessageFactory.CreateUserText("  修复   渲染错位 \n第二行不该进标题");

        Assert.Equal("修复 渲染错位", SessionTitleFromUserMessage.Derive(message));
    }

    [Fact]
    public void Derive_Caps_Length()
    {
        var message = MessageFactory.CreateUserText(new string('长', 80));

        var title = SessionTitleFromUserMessage.Derive(message);

        Assert.NotNull(title);
        Assert.Equal(SessionTitleFromUserMessage.MaxTitleLength, title.Length);
    }

    [Fact]
    public void Derive_Skips_Plugin_Messages_And_Empty_Text()
    {
        var plugin = MessageFactory.CreateUserText("ignore me", new PluginMessageSource("user-approval"));

        Assert.Null(SessionTitleFromUserMessage.Derive(plugin));
        Assert.Null(SessionTitleFromUserMessage.Derive(MessageFactory.CreateUserText("   \n\t ")));
    }

    [Fact]
    public async Task First_User_Message_Names_Session_Without_Replacing_Id()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var id = environment.Agent.Id;

        environment.Agent.Session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText("重构会话标题\n附带说明")),
            new SurfaceOp.Append());
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(environment.Agent.Session);

        var persistence = environment.App.Ctx.Get<ISessionPersistence>("sessionPersistence")!;
        var snapshot = persistence.Stat(id);

        Assert.NotNull(snapshot);
        Assert.Equal("重构会话标题", snapshot.Header.Title);
        Assert.Equal(id, snapshot.Header.Id);
        Assert.Equal("重构会话标题", environment.Agent.Session.Header.Title);
    }

    [Fact]
    public async Task Later_User_Messages_Keep_The_First_Title()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();

        environment.Agent.Session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText("第一个问题")),
            new SurfaceOp.Append());
        environment.Agent.Session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText("第二个问题")),
            new SurfaceOp.Append());
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(environment.Agent.Session);

        Assert.Equal("第一个问题", environment.Agent.Session.Header.Title);
    }
}
