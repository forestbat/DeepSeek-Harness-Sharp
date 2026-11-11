using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using Grid = Avalonia.Controls.Grid;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class AccountGuiHeadlessTests
{
    [Fact]
    public async Task SettingsAccountSection_RendersAndWiresRealService() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            viewModel.ShowSettingsCommand.Execute(null);
            var account = viewModel.Preferences.Sections.First(section => section.Section == SettingsSection.Account);
            account.SelectCommand!.Execute(account);
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Preferences.IsAccountSection);
            Assert.True(viewModel.Preferences.Account.IsAvailable);
            Assert.True(viewModel.Preferences.Account.CanSignIn);

            var settings = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "账号");
            // 不再有"启用账号登录"配置项: 登录与否由用户点按钮决定。
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<CheckBox>(),
                box => box.Content is string text && text.Contains("账号登录", StringComparison.Ordinal));

            var signIn = settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AccountSignIn");
            Assert.True(signIn.IsEffectivelyEnabled);
            Assert.Equal("登录到 DeepSeek", signIn.Content);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 标题栏「登录到 DeepSeek」在未登录时出现, 位置紧邻最小化按钮。 */
    [Fact]
    public async Task TitleBar_OffersSignInLeftOfMinimize() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var titleBar = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "TitleAccountSignIn");
            Assert.True(titleBar.IsEffectivelyVisible);
            Assert.Equal("登录到 DeepSeek", titleBar.Content);
            Assert.True(titleBar.Command!.CanExecute(null));

            var titleBarGrid = window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "TitleBar");
            var minimize = titleBarGrid.Children.OfType<Button>().Single(button => button.Name == "TitleMinimize");
            Assert.True(Grid.GetColumn(titleBar) < Grid.GetColumn(minimize));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}
