namespace Dsh.Pty;

public static class TerminalTextWidth
{
    public static int Of(char character) => IsWide(character) ? 2 : 1;

    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Of(text.AsSpan());
    }

    public static int Of(ReadOnlySpan<char> text)
    {
        var width = 0;
        foreach (var character in text)
            width += Of(character);
        return width;
    }

    public static bool IsWide(char character)
    {
        if (character < '\u1100')
            return false;
        return character switch
        {
            <= '\u115f' => true,
            >= '\u2e80' and <= '\ua4cf' => true,
            >= '\uac00' and <= '\ud7a3' => true,
            >= '\uf900' and <= '\ufaff' => true,
            >= '\ufe30' and <= '\ufe4f' => true,
            >= '\uff00' and <= '\uff60' => true,
            >= '\uffe0' and <= '\uffe6' => true,
            _ => false,
        };
    }
}
