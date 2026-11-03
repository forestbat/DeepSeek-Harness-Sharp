using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public class GuiTestEnvironmentTests
{
    [Fact]
    public async Task Reset_Closes_Retained_Session_Handles_And_Removes_Streamed_History()
    {
        ISessionHandle? writer = null;
        ISessionHandle? reader = null;
        try
        {
            string sessionDirectory;
            await using (var environment = await GuiTestEnvironment.CreateAsync())
            {
                await environment.OpenChatAsync();
                var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
                var header = environment.Agent.Session.Header with { Id = SessionId.Create($"retained-{Guid.NewGuid():N}") };
                var session = Session.Create(header.Id, header: header);
                session.Append(new TurnStartPayload(1));
                writer = persistence.Create(header);
                writer.Append(session.SnapshotEvents());
                reader = persistence.Open(header.Id, SessionAccess.Read);
                Assert.Single(reader.Read());
                session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "buffered history")));
                writer.Append(session.SnapshotEvents().Skip(1).ToArray());
                sessionDirectory = JsonlLayout.SessionDir(environment.App.Home.SessionsPath, header.Cwd, header.Id);
                Assert.True(Directory.Exists(sessionDirectory));
            }

            Assert.False(Directory.Exists(sessionDirectory));
            Assert.Throws<SessionHandleClosedException>(() => writer.Read());
            Assert.Throws<SessionHandleClosedException>(() => reader.Read());
            await using var next = await GuiTestEnvironment.CreateAsync();
            Assert.Single(next.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.List());
            Assert.Empty(next.Agent.Session.SnapshotEvents());
        }
        finally
        {
            reader?.Dispose();
            writer?.Dispose();
        }
    }

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
