using Dsh.Tui;

namespace Dsh.Tests;

public class TerminalTextWidthTests
{
    [Theory]
    [InlineData('a', 1)]
    [InlineData(' ', 1)]
    [InlineData('─', 1)]
    [InlineData('中', 2)]
    [InlineData('文', 2)]
    [InlineData('　', 2)]
    [InlineData('０', 2)]
    [InlineData('한', 2)]
    public void Width_Matches_Terminal_Cell_Rules(char character, int expected)
        => Assert.Equal(expected, TerminalTextWidth.Of(character));

    [Fact]
    public void String_Width_Sums_Character_Widths()
        => Assert.Equal(7, TerminalTextWidth.Of("ab中文c"));
}
