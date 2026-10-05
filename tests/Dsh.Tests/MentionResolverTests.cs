using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public class MentionResolverTests
{
    [Fact]
    public void File_Candidates_Are_Relative_By_Prefix()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(temp.Path, "beta.txt"), "b");

        var candidates = MentionResolver.ResolveCandidates("al", temp.Path, []);

        Assert.Equal(["alpha.txt"], candidates);
    }

    [Fact]
    public void Empty_Token_Lists_Working_Directory_Entries_Directories_First()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        File.WriteAllText(Path.Combine(temp.Path, "README.md"), "r");
        File.WriteAllText(Path.Combine(temp.Path, ".hidden"), "h");

        var candidates = MentionResolver.ResolveCandidates("", temp.Path, []);

        Assert.Equal(["src", ".hidden", "README.md"], candidates);
    }

    [Fact]
    public void Directory_Candidates_List_Children()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));

        var candidates = MentionResolver.ResolveCandidates("sr", temp.Path, []);

        Assert.Equal(["src"], candidates);
    }

    [Fact]
    public void Trailing_Separator_Lists_Directory_Children()
    {
        using var temp = new TempDir();
        var src = Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        Directory.CreateDirectory(Path.Combine(src.FullName, "nested"));
        File.WriteAllText(Path.Combine(src.FullName, "a.cs"), "a");

        var candidates = MentionResolver.ResolveCandidates("src/", temp.Path, []);

        Assert.Equal(["src/nested", "src/a.cs"], candidates);
    }

    [Fact]
    public void Sessions_And_Files_Are_Merged_For_NonEmpty_Token()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "session-file.txt"), "x");
        MentionSessionInfo[] sessions = [new("session-1", "Alpha", null)];

        var candidates = MentionResolver.ResolveCandidates("session", temp.Path, sessions);

        Assert.Equal(["session-1", "session-file.txt"], candidates);
    }

    [Fact]
    public void ExpandMentions_Expands_File_Content()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "note.txt"), "hello world");

        var expanded = MentionResolver.ExpandMentions("see @note.txt", temp.Path, []);

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

        var expanded = MentionResolver.ExpandMentions("list @docs", temp.Path, []);

        Assert.Contains("[directory: docs]", expanded);
        Assert.Contains("docs/a.md", expanded);
        Assert.Contains("docs/b.md", expanded);
    }

    [Fact]
    public void Session_Candidates_Filter_By_Id_Or_Title()
    {
        using var temp = new TempDir();
        MentionSessionInfo[] sessions =
        [
            new("session-1", "Alpha", null),
            new("session-2", "Beta", null),
        ];

        Assert.Equal(["session-1", "session-2"], MentionResolver.ResolveCandidates("session-", temp.Path, sessions));
        Assert.Equal(["session-1"], MentionResolver.ResolveCandidates("Alp", temp.Path, sessions));
        Assert.Equal(["session-2"], MentionResolver.ResolveCandidates("session-2", temp.Path, sessions));
    }

    [Fact]
    public void ExpandMentions_Expands_Session()
    {
        MentionSessionInfo[] sessions = [new("session-1", "Alpha", "first session summary")];

        var expanded = MentionResolver.ExpandMentions("see @session-1", "/tmp", sessions);

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

        var info = MentionSessionInfo.FromSnapshot(snapshot);

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
