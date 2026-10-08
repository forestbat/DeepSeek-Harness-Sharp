namespace Dsh.Tui;

public static class CellPacker
{
    // 布局: 0-15 字符, 16-21 前景槽位, 22-27 背景槽位, 28-30 样式(Bold=1, Dim=2, Reverse=4)。
    // 槽位是 CellColorTable 的 6-bit 索引(默认/16 色板/RGB), 24-bit 真彩经调色板间接进 GPU。
    public static uint Pack(Cell cell) => Pack(cell, CellColorTable.Shared);

    public static uint Pack(Cell cell, CellColorTable table)
        => (uint)(cell.Character
            | (table.Index(cell.Foreground) << 16)
            | (table.Index(cell.Background) << 22)
            | ((int)cell.Style << 28));

    public static void PackRows(CellGrid grid, int firstRow, int rowCount, uint[] destination, GlyphAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(atlas);
        var length = rowCount * grid.Width;
        if (destination.Length < length)
            throw new ArgumentException($"打包目标数组需要至少 {length} 个 uint", nameof(destination));
        var source = grid.RawCells.AsSpan(firstRow * grid.Width, length);
        ref var sourceRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source);
        ref var destinationRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(destination);
        ref var bitmapRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(atlas.BakedBitmap);
        for (var index = 0; index < length; index++)
        {
            ref readonly var cell = ref System.Runtime.CompilerServices.Unsafe.Add(ref sourceRef, index);
            var character = cell.Character;
            if (character is not ('\0' or ' ') && (System.Runtime.CompilerServices.Unsafe.Add(ref bitmapRef, character >> 3) & (1 << (character & 7))) == 0)
                atlas.BakeSlow(character);
            System.Runtime.CompilerServices.Unsafe.Add(ref destinationRef, index) = Pack(cell);
        }
    }

    public static int CollectDirtyRowRanges(CellGrid current, CellGrid? previous, List<(int Start, int Count)> ranges)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(ranges);
        ranges.Clear();
        if (previous is null || previous.Width != current.Width || previous.Height != current.Height)
        {
            ranges.Add((0, current.Height));
            return current.Height;
        }
        var dirtyRows = 0;
        var runStart = -1;
        for (var y = 0; y < current.Height; y++)
        {
            var equal = System.Runtime.InteropServices.MemoryMarshal.Cast<Cell, byte>(current.Row(y))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.Cast<Cell, byte>(previous.Row(y)));
            if (equal)
            {
                if (runStart >= 0)
                {
                    ranges.Add((runStart, y - runStart));
                    runStart = -1;
                }
                continue;
            }
            dirtyRows++;
            if (runStart < 0)
                runStart = y;
        }
        if (runStart >= 0)
            ranges.Add((runStart, current.Height - runStart));
        return dirtyRows;
    }
}
