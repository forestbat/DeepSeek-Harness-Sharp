using Dsh.Core;

namespace Dsh.Tests;

public sealed class MemoryDocumentTests
{
    [Fact]
    public void Parse_PreservesPreambleAndRawLines()
    {
        var doc = MemoryDocument.Parse("# Project notes\n\n## Facts\n- a :: 1 (2026-09-16T09:35:17Z)\nsome free text\n");

        Assert.Equal(["# Project notes", ""], doc.Preamble);
        var section = Assert.Single(doc.Sections);
        Assert.Equal("Facts", section.Name);
        Assert.Equal(2, section.Entries.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 9, 35, 17, TimeSpan.Zero), section.Entries[0].Record?.UpdatedAt);
        Assert.Equal("some free text", section.Entries[1].RawLine);
    }

    [Fact]
    public void Parse_ToleratesRecordWithoutTimestamp()
    {
        var doc = MemoryDocument.Parse("## Facts\n- a :: 1\n");

        var record = Assert.Single(doc.Sections[0].Records);
        Assert.Null(record.UpdatedAt);
        Assert.Equal("- a :: 1", record.Render());
    }

    [Fact]
    public void Render_RoundTrips()
    {
        const string text = "# Project notes\n\n## Facts\n- a :: 1 (2026-09-16T09:35:17Z)\n\n## Corrections\n- b :: 2\n";

        var doc = MemoryDocument.Parse(text);

        Assert.Equal(text, doc.Render());
    }

    [Fact]
    public void Upsert_ReplacesSameKeyInSection()
    {
        var doc = MemoryDocument.Parse("## Facts\n- a :: 1\n- b :: 2\n");

        var replaced = doc.Upsert("Facts", new MemoryRecord("A", "3", DateTimeOffset.UnixEpoch));

        Assert.True(replaced);
        var records = doc.Sections[0].Records.ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal("3", records[0].Text);
        Assert.Equal(DateTimeOffset.UnixEpoch, records[0].UpdatedAt);
    }

    [Fact]
    public void Upsert_AppendsToSectionEndAndCreatesSection()
    {
        var doc = MemoryDocument.Parse("## Facts\n- a :: 1\n");

        Assert.False(doc.Upsert("Facts", new MemoryRecord("b", "2", null)));
        Assert.False(doc.Upsert("Commands", new MemoryRecord("c", "3", null)));

        Assert.Equal(["a", "b"], doc.Sections[0].Records.Select(record => record.Key));
        Assert.Equal("Commands", doc.Sections[1].Name);
    }

    [Fact]
    public void Upsert_SameKeyInOtherSectionKeepsBoth()
    {
        var doc = MemoryDocument.Parse("## Facts\n- a :: 1\n");

        Assert.False(doc.Upsert("Corrections", new MemoryRecord("a", "2", null)));

        Assert.Equal("1", doc.FindSection("Facts")?.Records.Single().Text);
        Assert.Equal("2", doc.FindSection("Corrections")?.Records.Single().Text);
    }

    [Fact]
    public void RemoveWhere_DeletesAllMatchesAndHonorsSection()
    {
        var doc = MemoryDocument.Parse("## Facts\n- a :: 1\n- keep :: x\n\n## Corrections\n- a :: 2\n");

        var removed = doc.RemoveWhere(null, record => string.Equals(record.Key, "a", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Facts", "Corrections"], removed.Select(entry => entry.Section));
        Assert.Equal(["1", "2"], removed.Select(entry => entry.Record.Text));
        Assert.Equal("keep", doc.FindSection("Facts")?.Records.Single().Key);
        Assert.Empty(doc.FindSection("Corrections")!.Records);

        var scoped = doc.RemoveWhere("Facts", _ => true);

        Assert.Single(scoped);
        Assert.Equal("Facts", scoped[0].Section);
        Assert.Equal("keep", scoped[0].Record.Key);
    }

    [Theory]
    [InlineData("Corrections", MemoryKind.Correction)]
    [InlineData("Decisions", MemoryKind.Decision)]
    [InlineData("Constraints", MemoryKind.Constraint)]
    [InlineData("Commands", MemoryKind.Environment)]
    [InlineData("Environment", MemoryKind.Environment)]
    [InlineData("Facts", MemoryKind.Fact)]
    public void KindOf_MapsSectionNames(string section, MemoryKind expected)
        => Assert.Equal(expected, MemoryDocument.KindOf(section));
}
