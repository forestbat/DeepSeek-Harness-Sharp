using Dsh.Gui.Services;

namespace Dsh.Tests;

public sealed class SessionCatalogTests
{
    [Fact]
    public void WorkspacePath_NormalizesOrFallsBackToPlaceholder()
    {
        Assert.Equal(SessionCatalog.UnspecifiedWorkspace, SessionCatalog.WorkspacePath(null));
        Assert.Equal(SessionCatalog.UnspecifiedWorkspace, SessionCatalog.WorkspacePath("  "));

        var path = SessionCatalog.WorkspacePath(Path.Combine(Path.GetTempPath(), "dsh-ws", "..", "dsh-ws"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dsh-ws")), path);
    }

    [Fact]
    public void WorkspaceDisplayName_UsesLastSegment()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dsh-ws-display"));
        Assert.Equal("dsh-ws-display", SessionCatalog.WorkspaceDisplayName(root));
        Assert.Equal(SessionCatalog.UnspecifiedWorkspace, SessionCatalog.WorkspaceDisplayName(SessionCatalog.UnspecifiedWorkspace));
    }
}
