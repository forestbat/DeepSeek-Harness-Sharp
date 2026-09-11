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
            descriptor => descriptor.Name == "remove" ? ["alpha", "beta"] : []);
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
            descriptor => descriptor.Name == "model"
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
            descriptor => descriptor.Name == "remove" ? ["alpha"] : []);
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

        Assert.Equal("/provider add acme --base-url https://example.com --api-key secret --type anthropic --model-ids claude-3", completed);
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
}