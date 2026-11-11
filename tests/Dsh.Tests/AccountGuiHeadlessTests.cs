using Avalonia;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Account;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Button = Avalonia.Controls.Button;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;
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

    /**
     * 标题栏账号按钮: 在最小化左边、文字垂直居中、点击能进登录态。
     * 按钮必须带 DecorationsElement 角色——扩展客户区下这条是窗口拖拽区, 没有角色就收不到点击。
     */
    [Fact]
    public async Task TitleBar_AccountButtonIsCenteredClickableAndLeftOfMinimize() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = new MainWindow(environment.App, await environment.OpenChatAsync()) { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TitleAccount");
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("登录到 DeepSeek", button.Content);
            Assert.Equal(WindowDecorationProperties.GetElementRole(button), WindowDecorationsElementRole.DecorationsElement);
            Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);

            // 上下留白对称: 按钮在标题栏里垂直居中, 不是贴着上沿。
            var titleBarGrid = window.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "TitleBar");
            var top = button.TranslatePoint(new Point(0, 0), titleBarGrid)!.Value.Y;
            var bottom = titleBarGrid.Bounds.Height - (top + button.Bounds.Height);
            Assert.InRange(Math.Abs(top - bottom), 0, 1.5);

            var minimize = titleBarGrid.Children.OfType<Button>().Single(b => b.Name == "TitleMinimize");
            Assert.True(Grid.GetColumn(button) < Grid.GetColumn(minimize));

            // 点击直接进入登录流程, 不需要先去设置页。
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var account = window.ViewModel!.Preferences.Account;
            Assert.True(account.IsWaiting);
            Assert.Equal("登录中…", account.TitleText);
            await account.CancelCommand.ExecuteAsync(null);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 登录成功后按钮仍在, 只是换成账号名: 它不能因为登录了就消失。 */
    [Fact]
    public async Task TitleBar_AccountButtonSurvivesSignIn() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = new MainWindow(environment.App, await environment.OpenChatAsync()) { Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var account = window.ViewModel!.Preferences.Account;
            account.DisplayName = "小明";
            account.Stage = AccountPhase.SignedIn;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TitleAccount");
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("小明", button.Content);
            Assert.True(button.Classes.Contains("signedIn"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}
