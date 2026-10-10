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
            // 符号/表情里东亚宽度判为 W 的区段: 终端按两格渲染, 少算会让后续列整体左移、把分屏分隔线画歪。
            >= '\u231a' and <= '\u231b' => true,
            >= '\u2329' and <= '\u232a' => true,
            >= '\u23e9' and <= '\u23ec' => true,
            '\u23f0' => true,
            '\u23f3' => true,
            >= '\u25fd' and <= '\u25fe' => true,
            >= '\u2614' and <= '\u2615' => true,
            >= '\u2648' and <= '\u2653' => true,
            '\u267f' => true,
            '\u2693' => true,
            '\u26a1' => true,
            >= '\u26aa' and <= '\u26ab' => true,
            >= '\u26bd' and <= '\u26be' => true,
            >= '\u26c4' and <= '\u26c5' => true,
            '\u26ce' => true,
            '\u26d4' => true,
            '\u26ea' => true,
            >= '\u26f2' and <= '\u26f3' => true,
            '\u26f5' => true,
            '\u26fa' => true,
            '\u26fd' => true,
            '\u2705' => true,
            >= '\u270a' and <= '\u270b' => true,
            '\u2728' => true,
            '\u274c' => true,
            '\u274e' => true,
            >= '\u2753' and <= '\u2755' => true,
            '\u2757' => true,
            >= '\u2795' and <= '\u2797' => true,
            '\u27b0' => true,
            '\u27bf' => true,
            >= '\u2b1b' and <= '\u2b1c' => true,
            '\u2b50' => true,
            '\u2b55' => true,
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
