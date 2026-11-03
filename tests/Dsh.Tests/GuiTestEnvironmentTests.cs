using Dsh.Boot;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public class GuiTestEnvironmentTests
{
    [Fact]
    public async Task Reset_Restores_Settings_Without_Changing_A_Held_Snapshot()
    {
        FileStream? snapshot = null;
        try
        {
            string path;
            string original;
            string modified;
            await using (var environment = await GuiTestEnvironment.CreateAsync())
            {
                path = environment.App.Home.SettingsFile;
                original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                modified = $"{original}{Environment.NewLine}# held reader snapshot{Environment.NewLine}";
                await HarnessSettings.CommitApprovedMutationAsync(path, original, modified,
                    (temporary, signal) => File.WriteAllTextAsync(temporary, modified, signal), TestContext.Current.CancellationToken);
                snapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }

            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            using var reader = new StreamReader(snapshot, leaveOpen: true);
            Assert.Equal(modified, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.DisposeAsync();
        }
    }
}
