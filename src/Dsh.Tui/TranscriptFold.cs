namespace Dsh.Tui;

public sealed class TranscriptFold
{
    public required int Start { get; init; }

    public required int End { get; set; }

    public required string Label { get; init; }

    public required string Preview { get; init; }

    public bool Collapsed { get; set; } = true;
}