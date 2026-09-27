using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Tests;

public sealed class DiffCardExtractorTests
{
    [Fact]
    public void MetaLayer_ConsumesExplicitDiffCard()
    {
        var meta = JsonDocument.Parse("""
            {
              "card": "diff",
              "title": "str_replace src/Foo.cs",
              "diffs": [{ "path": "src/Foo.cs", "oldText": "a\nb\nc", "newText": "a\nx\nc" }]
            }
            """).RootElement;

        var card = DiffCardExtractor.TryExtract("str_replace_editor", null, meta);

        Assert.NotNull(card);
        Assert.Equal("str_replace src/Foo.cs", card.Title);
        Assert.Equal("meta", card.Source);
        Assert.Equal(1, card.Added);
        Assert.Equal(1, card.Removed);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "a"),
                new DiffLine(DiffLineKind.Delete, 2, null, "b"),
                new DiffLine(DiffLineKind.Add, null, 2, "x"),
                new DiffLine(DiffLineKind.Context, 3, 3, "c"),
            ],
            card.Lines);
    }

    [Fact]
    public void MetaLayer_WinsOverPatchArguments()
    {
        var meta = JsonDocument.Parse("""
            { "card": "diff", "title": "edit a.cs", "diffs": [{ "path": "a.cs", "oldText": "old", "newText": "new" }] }
            """).RootElement;
        var arguments = """{"input":"*** Begin Patch\n*** Update File: other.cs\n@@\n-x\n+y\n*** End Patch"}""";

        var card = DiffCardExtractor.TryExtract("edit", arguments, meta);

        Assert.NotNull(card);
        Assert.Equal("meta", card.Source);
        Assert.Equal("edit a.cs", card.Title);
    }

    [Fact]
    public void MetaLayer_SupportsCreateWithNullOldText()
    {
        var meta = JsonDocument.Parse("""
            { "card": "diff", "title": "create b.cs", "diffs": [{ "path": "b.cs", "oldText": null, "newText": "one\ntwo" }] }
            """).RootElement;

        var card = DiffCardExtractor.TryExtract("write", null, meta);

        Assert.NotNull(card);
        Assert.Equal(2, card.Added);
        Assert.Equal(0, card.Removed);
        Assert.All(card.Lines, line => Assert.Equal(DiffLineKind.Add, line.Kind));
    }

    [Fact]
    public void PatchLayer_ParsesUnifiedDiffFromArguments()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            patch = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -2,2 +2,2 @@\n keep\n-old\n+new",
        });

        var card = DiffCardExtractor.TryExtract("apply_patch", arguments, null);

        Assert.NotNull(card);
        Assert.Equal("patch", card.Source);
        Assert.Equal("src/Foo.cs", card.Title);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 2, 2, "keep"),
                new DiffLine(DiffLineKind.Delete, 3, null, "old"),
                new DiffLine(DiffLineKind.Add, null, 3, "new"),
            ],
            card.Lines);
    }

    [Fact]
    public void PatchLayer_ParsesApplyPatchFormat()
    {
        var arguments = """{"input":"*** Begin Patch\n*** Update File: src/Foo.cs\n@@\n context line\n-removed line\n+added line\n*** End Patch"}""";

        var card = DiffCardExtractor.TryExtract("apply_patch", arguments, null);

        Assert.NotNull(card);
        Assert.Equal("patch", card.Source);
        Assert.Equal("src/Foo.cs", card.Title);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "context line"),
                new DiffLine(DiffLineKind.Delete, 2, null, "removed line"),
                new DiffLine(DiffLineKind.Add, null, 2, "added line"),
            ],
            card.Lines);
    }

    [Fact]
    public void PatchLayer_ScansResultTextWhenArgumentsHaveNoPatch()
    {
        var resultText = "*** Begin Patch\n*** Add File: new.cs\n+first\n+second\n*** End Patch";

        var card = DiffCardExtractor.TryExtract("some_mcp_tool", """{"query":"nothing"}""", null, resultText);

        Assert.NotNull(card);
        Assert.Equal("new.cs", card.Title);
        Assert.Equal(2, card.Added);
    }

    [Fact]
    public void PatchLayer_IgnoresTextWithoutPatchMarkers()
    {
        var card = DiffCardExtractor.TryExtract("bash", """{"command":"echo hello"}""", null, "hello\nworld");

        Assert.Null(card);
    }

    [Fact]
    public void PairsLayer_DiffsOldStringNewString()
    {
        var arguments = """{"file_path":"src/Foo.cs","old_string":"line1\nline2","new_string":"line1\nchanged"}""";

        var card = DiffCardExtractor.TryExtract("edit", arguments, null);

        Assert.NotNull(card);
        Assert.Equal("pairs", card.Source);
        Assert.Equal("edit src/Foo.cs", card.Title);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "line1"),
                new DiffLine(DiffLineKind.Delete, 2, null, "line2"),
                new DiffLine(DiffLineKind.Add, null, 2, "changed"),
            ],
            card.Lines);
    }

    [Fact]
    public void PairsLayer_AcceptsPascalCaseBeforeAfter()
    {
        var arguments = """{"Before":"a","After":"b"}""";

        var card = DiffCardExtractor.TryExtract("tool", arguments, null);

        Assert.NotNull(card);
        Assert.Equal(1, card.Added);
        Assert.Equal(1, card.Removed);
    }

    [Fact]
    public void PairsLayer_ReadsResultMetaPairs()
    {
        var meta = JsonDocument.Parse("""{ "before": "x", "after": "y" }""").RootElement;

        var card = DiffCardExtractor.TryExtract("tool", null, meta);

        Assert.NotNull(card);
        Assert.Equal("pairs", card.Source);
    }

    [Fact]
    public void PairsLayer_IdenticalOldAndNewYieldsNull()
    {
        var card = DiffCardExtractor.TryExtract("edit", """{"old_string":"same","new_string":"same"}""", null);

        Assert.Null(card);
    }

    [Fact]
    public void NothingMatches_ReturnsNull()
    {
        Assert.Null(DiffCardExtractor.TryExtract("bash", """{"command":"ls -la"}""", null, "total 0"));
        Assert.Null(DiffCardExtractor.TryExtract(null, null, null));
    }
}

