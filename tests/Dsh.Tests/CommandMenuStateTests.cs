using Dsh.Interaction;
using Dsh.Tui;

namespace Dsh.Tests;

public class CommandMenuStateTests
{
    private static readonly CommandDescriptor[] Commands =
    [
        new("model", "List or switch model", MenuSchema: new CommandMenuSchema("Model")),
        new("mcp", "Manage MCP servers"),
        new("provider", "Manage providers", Subcommands:
        [
            new("add", "Add provider"),
            new("list", "List providers"),
            new("remove", "Remove provider", MenuSchema: new CommandMenuSchema("Provider")),
        ]),
    ];

    [Fact]
    public void Slash_Shows_All_Commands()
    {
        var state = new CommandMenuState(Commands);
        state.ApplyInput("/");

        Assert.Equal(["model", "mcp", "provider"], state.Candidates);
    }

    [Fact]
    public void Slash_m_Filters_Commands()
    {
        var state = new CommandMenuState(Commands);
        state.ApplyInput("/m");

        Assert.Equal(["model", "mcp"], state.Candidates);
    }

    [Fact]
    public void Esc_From_Root_Closes_Menu()
    {
        var state = new CommandMenuState(Commands);

        Assert.True(state.Back());
        Assert.False(state.IsActive);
    }

    [Fact]
    public void Enter_On_Command_Without_Arguments_Returns_Command()
    {
        var state = new CommandMenuState(Commands);
        state.ApplyInput("/mcp");

        Assert.Equal("/mcp", state.Confirm());
    }

    [Fact]
    public void Enter_On_Provider_Enters_Subcommands()
    {
        var state = new CommandMenuState(Commands);
        state.ApplyInput("/provider");

        Assert.Null(state.Confirm());
        Assert.Equal(CommandMenuState.MenuStage.Subcommand, state.Stage);
        Assert.Equal(["add", "list", "remove"], state.Candidates);
    }

    [Fact]
    public void Space_After_Provider_Auto_Enters_Subcommands()
    {
        var state = new CommandMenuState(Commands);
        state.ApplyInput("/provider ");

        Assert.Equal(CommandMenuState.MenuStage.Subcommand, state.Stage);
        Assert.Equal(["add", "list", "remove"], state.Candidates);
    }

    [Fact]
    public void Provider_Remove_Uses_Injected_Provider_Candidates()
    {
        var state = new CommandMenuState(
            Commands,
            (_, subcommand, _) => subcommand?.Name == "remove" ? ["alpha", "beta"] : []);
        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider remove");
        state.Confirm();

        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);
        Assert.Equal(["alpha", "beta"], state.Candidates);
        state.ApplyInput("/provider remove alp");
        Assert.Equal(["alpha"], state.Candidates);
        Assert.Equal("/provider remove alpha", state.Confirm());
    }

    [Fact]
    public void Model_Search_Filters_And_Completes_Full_Command()
    {
        var state = new CommandMenuState(
            Commands,
            (descriptor, _, _) => descriptor.Name == "model"
                ? ["deepseek/deepseek-v4", "openai/gpt-5"]
                : []);
        state.ApplyInput("/model");
        state.Confirm();

        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);
        state.ApplyInput("/model deep");
        Assert.Equal(["deepseek/deepseek-v4"], state.Candidates);
        Assert.Equal("/model deepseek/deepseek-v4", state.Confirm());
    }

    [Fact]
    public void Back_From_Argument_Returns_To_Previous_Stage()
    {
        var state = new CommandMenuState(
            Commands,
            (_, subcommand, _) => subcommand?.Name == "remove" ? ["alpha"] : []);
        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider remove");
        state.Confirm();
        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);

        Assert.True(state.Back());
        Assert.Equal(CommandMenuState.MenuStage.Subcommand, state.Stage);
        Assert.Equal(["add", "list", "remove"], state.Candidates);
    }

    [Fact]
    public void Catalog_Adds_Session_Command_When_Not_Registered()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("model", "List or switch model")]);

        Assert.Contains(descriptors, descriptor => descriptor.Name == "session");
    }

    [Fact]
    public void Provider_Add_Wizard_Collects_Flagged_Arguments()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("provider", "Manage providers")]);
        var state = new CommandMenuState(descriptors);

        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider add");
        state.Confirm();

        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);
        Assert.Equal(0, state.ArgumentIndex);
        Assert.Equal("Provider name", state.Prompt);

        state.ApplyInput("/provider add acme");
        Assert.Null(state.Confirm());
        Assert.Equal(1, state.ArgumentIndex);

        state.ApplyInput("/provider add https://example.com");
        Assert.Null(state.Confirm());
        Assert.Equal(2, state.ArgumentIndex);

        state.ApplyInput("/provider add secret");
        Assert.Null(state.Confirm());
        Assert.Equal(3, state.ArgumentIndex);

        state.ApplyInput("/provider add anthropic");
        Assert.Null(state.Confirm());
        Assert.Equal(4, state.ArgumentIndex);

        state.ApplyInput("/provider add claude-3");
        var completed = state.Confirm();

        Assert.Equal("/provider add acme --base-url https://example.com --api-key secret --type anthropic-messages --model-ids claude-3", completed);
    }

    /** 预置值(目录 provider 带出的 baseUrl/type/models)在输入行为空时被采纳, 可选参数可空回车跳过。 */
    [Fact]
    public void Prefill_Is_Accepted_On_Empty_Input_And_Optional_Argument_Is_Skipped()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("provider", "Manage providers")]);
        var state = new CommandMenuState(descriptors);

        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider add");
        state.Confirm();
        state.Prefill("base-url", "https://api.deepseek.com");
        state.Prefill("type", "deepseek");

        state.ApplyInput("/provider add acme");
        Assert.Null(state.Confirm());
        Assert.Equal("https://api.deepseek.com", state.PrefilledValue(1));
        // base-url/type 已有预置值 → 回车直接跳过它们, 落到唯一还需输入的必填 api-key。
        Assert.Equal(2, state.ArgumentIndex);

        state.ApplyInput("/provider add secret");
        Assert.Null(state.Confirm());
        // type 有预置值被跳过; model-ids 可选且无值, 回车停在这里等用户决定填或跳过。
        Assert.Equal(4, state.ArgumentIndex);

        var completed = state.Confirm();

        Assert.Equal("/provider add acme --base-url https://api.deepseek.com --api-key secret --type deepseek", completed);
    }

    /** Tab 在所有参数之间循环(含可选, 便于回头修改), 走到最后一条再按回到第一条; 回看已填参数时空回车保留原值。 */
    [Fact]
    public void Tab_Cycles_All_Arguments_And_Keeps_Recorded_Values()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("provider", "Manage providers")]);
        var state = new CommandMenuState(descriptors, (_, _, schema) =>
            string.Equals(schema?.Name, "name", StringComparison.Ordinal) ? ["302ai", "acme", "zhipuai"] : []);

        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider add");
        state.Confirm();
        state.ApplyInput("/provider add acme");
        Assert.Null(state.Confirm());
        Assert.Equal(1, state.ArgumentIndex);

        // 可选参数也必须在循环里, 否则用户改不了 --type / --model-ids。
        Assert.True(state.MoveToNextArgument());
        Assert.Equal(2, state.ArgumentIndex);
        Assert.True(state.MoveToNextArgument());
        Assert.Equal(3, state.ArgumentIndex);
        Assert.True(state.MoveToNextArgument());
        Assert.Equal(4, state.ArgumentIndex);
        Assert.True(state.MoveToNextArgument());
        Assert.Equal(0, state.ArgumentIndex);

        // 回到 name: 空回车保留已记录的 acme, 不会被候选列表默认高亮的 302ai 覆盖。
        Assert.Null(state.Confirm());
        Assert.Equal(1, state.ArgumentIndex);

        state.ApplyInput("/provider add https://example.com");
        Assert.Null(state.Confirm());
        state.ApplyInput("/provider add secret");
        Assert.Null(state.Confirm());
        state.ApplyInput("/provider add anthropic-messages");
        Assert.Null(state.Confirm());
        Assert.Equal(4, state.ArgumentIndex);
        state.ApplyInput("/provider add claude-3");

        Assert.Equal(
            "/provider add acme --base-url https://example.com --api-key secret --type anthropic-messages --model-ids claude-3",
            state.Confirm());
    }

    [Fact]
    public void Back_From_Second_Argument_Returns_To_First()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("provider", "Manage providers")]);
        var state = new CommandMenuState(descriptors);

        state.ApplyInput("/provider");
        state.Confirm();
        state.ApplyInput("/provider add");
        state.Confirm();
        state.ApplyInput("/provider add acme");
        state.Confirm();

        Assert.Equal(1, state.ArgumentIndex);
        Assert.True(state.Back());
        Assert.Equal(0, state.ArgumentIndex);
    }

    [Fact]
    public void Reasoning_Menu_Filters_And_Completes_Command()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("reasoning", "Show or set reasoning effort")]);
        var state = new CommandMenuState(descriptors,
            (descriptor, _, _) => descriptor.Name == "reasoning" ? ["low", "high"] : []);

        state.ApplyInput("/reasoning");
        Assert.Null(state.Confirm());
        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);
        Assert.Equal(["low", "high"], state.Candidates);

        state.ApplyInput("/reasoning hig");
        Assert.Equal(["high"], state.Candidates);
        Assert.Equal("/reasoning high", state.Confirm());
    }

    [Fact]
    public void Skill_Menu_Uses_Injected_Candidates()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("skill", "List or inspect a skill")]);
        var state = new CommandMenuState(descriptors,
            (descriptor, _, _) => descriptor.Name == "skill" ? ["review-code", "ship-it"] : []);

        state.ApplyInput("/skill");
        Assert.Null(state.Confirm());
        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);

        state.ApplyInput("/skill ship");
        Assert.Equal(["ship-it"], state.Candidates);
        Assert.Equal("/skill ship-it", state.Confirm());
    }

    [Fact]
    public void Session_Menu_Lists_Injected_Session_Ids()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("session", "List or delete persistent sessions")]);
        var state = new CommandMenuState(descriptors,
            (descriptor, _, _) => descriptor.Name == "session" ? ["session-aaa", "session-bbb"] : []);

        state.ApplyInput("/session ");
        Assert.Equal(CommandMenuState.MenuStage.Argument, state.Stage);
        Assert.Equal(["session-aaa", "session-bbb"], state.Candidates);

        state.MoveDown();
        Assert.Equal("/session session-bbb", state.Confirm());
    }

    [Fact]
    public void Argument_Menu_Scrolls_Through_Large_Candidate_List()
    {
        var descriptors = CommandMenuCatalog.Enrich([new CommandDescriptor("model", "List or switch model")]);
        var state = new CommandMenuState(descriptors,
            (_, _, _) => Enumerable.Range(0, 500).Select(index => $"provider/m{index:000}").ToList());

        state.ApplyInput("/model");
        state.Confirm();
        Assert.Equal(500, state.Candidates.Count);

        for (var index = 0; index < 300; index++)
            state.MoveDown();
        Assert.Equal("/model provider/m300", state.Confirm());
    }
}