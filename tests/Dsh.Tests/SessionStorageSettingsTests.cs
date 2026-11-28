using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Tests;

/** `storage.session_store`: 会话后端的选择与库文件位置。 */
public sealed class SessionStorageSettingsTests
{
    [Fact]
    public void Default_IsJsonl()
    {
        var settings = HarnessSettings.Parse("logging: { level: info }");
        Assert.Null(settings.Storage?.SessionStore);
        var storage = new SessionStorageSettings();
        Assert.False(storage.UsesDatabase);
        Assert.Equal("sync", storage.Io);
    }

    [Fact]
    public void DatabaseBackend_IsParsedFromSettings()
    {
        var settings = HarnessSettings.Parse("""
            storage:
              sessions: sessions
              session_store:
                backend: database
                database: state/sessions.db
                lanes: 2
                io: io_uring
            """);
        var store = Assert.IsType<SessionStoreSettings>(settings.Storage?.SessionStore);
        Assert.Equal("database", store.Backend);
        Assert.Equal("state/sessions.db", store.Database);
        Assert.Equal(2, store.Lanes);
        Assert.Equal("io_uring", store.Io);
        // 目录分区仍然照旧, 两个键互不影响。
        Assert.Equal("sessions", settings.Storage?.Sessions);
    }

    [Fact]
    public void DatabasePath_DefaultsUnderRoot_AndExpandsRelativePaths()
    {
        var paths = DshPaths.Resolve(Path.Combine(Path.GetTempPath(), "dsh-store-root"));
        Assert.Equal(Path.Combine(paths.Root, "sessions.db"),
            new SessionStorageSettings { Backend = SessionStorageSettings.DatabaseBackend }.ResolveDatabasePath(paths));
        Assert.Equal(Path.Combine(paths.Root, "state", "sessions.db"),
            new SessionStorageSettings { Database = "state/sessions.db" }.ResolveDatabasePath(paths));
        Assert.Equal(Path.GetFullPath("/absolute/sessions.db"),
            new SessionStorageSettings { Database = "/absolute/sessions.db" }.ResolveDatabasePath(paths));
    }
}

