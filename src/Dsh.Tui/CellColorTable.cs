namespace Dsh.Tui;

/**
 * GPU 调色板: 把每个 CellColor 映射成 6-bit 槽位(0=默认, 1..16=16 色板, 17..63=运行期分配的 RGB)。
 * 索引在一帧内稳定、跨帧复用, 因此增量上传的脏行不会因槽位漂移而串色。容量 64 对主题色足够;
 * 超出容量时回退到默认色(index 0)。
 */
public sealed class CellColorTable
{
    public const int Capacity = 64;

    public const int BaseColors = 17;

    public static CellColorTable Shared { get; } = new();

    private readonly CellColor[] _colors = new CellColor[Capacity];
    private readonly Dictionary<CellColor, int> _index = [];
    private readonly Lock _gate = new();
    private int _count;
    private int _version;

    public CellColorTable() => Reset();

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public int Version => Volatile.Read(ref _version);

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_colors);
            _index.Clear();
            _colors[0] = CellColor.Default;
            _index[CellColor.Default] = 0;
            for (var slot = 1; slot <= 16; slot++)
            {
                var color = CellColor.FromPalette((AnsiColor)slot);
                _colors[slot] = color;
                _index[color] = slot;
            }

            _count = BaseColors;
            _version++;
        }
    }

    public int Index(CellColor color)
    {
        if (color.IsDefault)
            return 0;
        if (color.IsPalette)
            return color.Palette;
        lock (_gate)
        {
            if (_index.TryGetValue(color, out var existing))
                return existing;
            if (_count >= Capacity)
                return 0;
            var slot = _count++;
            _colors[slot] = color;
            _index[color] = slot;
            _version++;
            return slot;
        }
    }

    public CellColor this[int index]
    {
        get
        {
            lock (_gate)
                return _colors[index];
        }
    }
}
