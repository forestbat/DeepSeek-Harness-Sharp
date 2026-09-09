using Dsh.Core;
using Dsh.Llm;
using Dsh.Tui;

namespace Dsh.Tests;

public class MentionResolverTests
{
    [Fact]
    public void File_Candidates_Are_Relative_By_Prefix()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(temp.Path, "beta.txt"), "b");
        var resolver = new MentionResolver();

        var candidates = resolver.ResolveCandidates("al", temp.Path, []);

        Assert.Equal(["alpha.txt"], candidates);
    }

    [Fact]
    public void Directory_Candidates_List_Children()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        var resolver = new MentionResolver();

        var candidates = resolver.ResolveCandidates("sr", temp.Path, []);

        Assert.Equal(["src"], candidates);
    }

    [Fact]
    public void ExpandMentions_Expands_File_Content()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "note.txt"), "hello world");
        var resolver = new MentionResolver();

        var expanded = resolver.ExpandMentions("see @note.txt", temp.Path);

        Assert.Contains("[file: note.txt]", expanded);
        Assert.Contains("hello world", expanded);
    }

    [Fact]
    public void ExpandMentions_Expands_Directory_Listing()
    {
        using var temp = new TempDir();
        var docs = Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        File.WriteAllText(Path.Combine(docs.FullName, "a.md"), "a");
        File.WriteAllText(Path.Combine(docs.FullName, "b.md"), "b");
        var resolver = new MentionResolver();

        var expanded = resolver.ExpandMentions("list @docs", temp.Path);

        Assert.Contains("[directory: docs]", expanded);
        Assert.Contains("docs/a.md", expanded);
        Assert.Contains("docs/b.md", expanded);
    }

    [Fact]
    public void Session_Candidates_Filter_By_Id_Or_Title()
    {
        var sessions = new[]
        {
            new SessionInfo("session-1", "Alpha", null),
            new SessionInfo("session-2", "Beta", null),
        };
        var resolver = new MentionResolver();

        Assert.Equal(["session-1", "session-2"], resolver.ResolveCandidates("session-", "/tmp", sessions));
        Assert.Equal(["session-1"], resolver.ResolveCandidates("Alp", "/tmp", sessions));
        Assert.Equal(["session-2"], resolver.ResolveCandidates("session-2", "/tmp", sessions));
    }

    [Fact]
    public void ExpandMentions_Expands_Session()
    {
        var sessions = new[]
        {
            new SessionInfo("session-1", "Alpha", "first session summary"),
        };
        var resolver = new MentionResolver();

        var expanded = resolver.ExpandMentions("see @session-1", "/tmp", sessions);

        Assert.Contains("session-1", expanded);
        Assert.Contains("first session summary", expanded);
    }

    [Fact]
    public void FromSnapshot_Maps_Header_Title()
    {
        var id = SessionId.Create("session-1");
        var header = new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1,
            IsSeeded = false,
            Title = "My session title",
        };
        var snapshot = new SessionPersistenceSnapshot
        {
            Header = header,
            Revision = "r1",
        };

        var info = SessionInfo.FromSnapshot(snapshot);

        Assert.Equal("session-1", info.Id);
        Assert.Equal("My session title", info.Title);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dsh-mention-tests-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}