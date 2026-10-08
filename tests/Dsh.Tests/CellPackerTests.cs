using Dsh.Tui;

namespace Dsh.Tests;

public class CellPackerTests
{
    [Fact]
    public void Pack_Encodes_Character_Colors_And_Style()
    {
        var packed = CellPacker.Pack(new Cell('A', AnsiColor.Green, AnsiColor.Black, CellStyle.Bold));

        Assert.Equal((uint)('A' | ((int)AnsiColor.Green << 16) | ((int)AnsiColor.Black << 22) | (1 << 28)), packed);
    }

    [Fact]
    public void Pack_Encodes_Rgb_Through_Color_Table()
    {
        var table = new CellColorTable();
        var color = CellColor.FromRgb(0x12, 0x34, 0x56);

        var packed = CellPacker.Pack(new Cell('Z', color, CellColor.Default), table);

        var slot = table.Index(color);
        Assert.InRange(slot, CellColorTable.BaseColors, CellColorTable.Capacity - 1);
        Assert.Equal((uint)('Z' | (slot << 16)), packed);
    }

    [Fact]
    public void Pack_Default_Cell_Is_Zero()
    {
        Assert.Equal(0u, CellPacker.Pack(default));
    }

    [Fact]
    public void PackRows_Packs_Requested_Rows_Only()
    {
        var grid = new CellGrid(3, 3);
        grid[0, 0] = new Cell('a');
        grid[0, 1] = new Cell('b');
        grid[2, 2] = new Cell('c');
        var destination = new uint[3];

        CellPacker.PackRows(grid, 1, 1, destination, new GlyphAtlas(Path.Combine(AppContext.BaseDirectory, "cellpacker-test-cache.bin")));

        Assert.Equal(CellPacker.Pack(new Cell('b')), destination[0]);
        Assert.Equal(0u, destination[1]);
        Assert.Equal(0u, destination[2]);
    }

    [Fact]
    public void CollectDirtyRowRanges_Full_When_No_Previous()
    {
        var current = new CellGrid(4, 5);
        var ranges = new List<(int Start, int Count)>();

        var dirty = CellPacker.CollectDirtyRowRanges(current, null, ranges);

        Assert.Equal(5, dirty);
        Assert.Equal([(0, 5)], ranges);
    }

    [Fact]
    public void CollectDirtyRowRanges_Merges_Adjacent_And_Splits_Gaps()
    {
        var current = new CellGrid(4, 6);
        var previous = new CellGrid(4, 6);
        current.CopyTo(previous);
        current[0, 1] = new Cell('x');
        current[0, 2] = new Cell('x');
        current[0, 4] = new Cell('x');
        var ranges = new List<(int Start, int Count)>();

        var dirty = CellPacker.CollectDirtyRowRanges(current, previous, ranges);

        Assert.Equal(3, dirty);
        Assert.Equal([(1, 2), (4, 1)], ranges);
    }

    [Fact]
    public void CollectDirtyRowRanges_Zero_When_Identical()
    {
        var current = new CellGrid(4, 3);
        current[0, 0] = new Cell('x');
        var previous = current.Clone();
        var ranges = new List<(int Start, int Count)>();

        var dirty = CellPacker.CollectDirtyRowRanges(current, previous, ranges);

        Assert.Equal(0, dirty);
        Assert.Empty(ranges);
    }
}
