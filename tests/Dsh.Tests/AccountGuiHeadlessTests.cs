using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class AccountGuiHeadlessTests
{
    [Fact]
    public async Task SettingsAccountSection_RendersAndWiresRealService() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        var window = new MainWindow(environment.App, environment.Agent);
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
            Assert.False(viewModel.Preferences.Account.IsEnabled);
            Assert.False(viewModel.Preferences.Account.CanSignIn);

            var settings = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "账号");

            viewModel.Preferences.Account.IsEnabled = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.Preferences.Account.CanSignIn);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}
