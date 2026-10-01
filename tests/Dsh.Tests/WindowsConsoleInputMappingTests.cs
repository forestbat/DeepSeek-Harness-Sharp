using Dsh.Tui;

namespace Dsh.Tests;

/** Windows 控制台输入记录到统一输入事件的映射(纯函数, 不需要真实控制台)。 */
public class WindowsConsoleInputMappingTests
{
    private const uint ShiftPressed = 0x0010;
    private const uint LeftAltPressed = 0x0002;
    private const uint LeftCtrlPressed = 0x0008;
    private const uint MouseMoved = 0x0001;
    private const uint MouseWheeled = 0x0004;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyA = 0x41;
    private const ushort VirtualKeyC = 0x43;
    private const ushort VirtualKeyUp = 0x26;

    [Fact]
    public void Letter_Key_Maps_To_Letter_With_Char()
    {
        var key = Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyA, 'a', 0, keyDown: true);

        Assert.NotNull(key);
        Assert.Equal(ConsoleKey.A, key.Value.Key);
        Assert.Equal('a', key.Value.KeyChar);
    }

    [Fact]
    public void Arrow_Key_Maps_Without_Char()
    {
        var key = Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyUp, '\0', 0, keyDown: true);

        Assert.NotNull(key);
        Assert.Equal(ConsoleKey.UpArrow, key.Value.Key);
        Assert.Equal('\0', key.Value.KeyChar);
    }

    [Fact]
    public void Control_Modifier_Is_Reported()
    {
        var key = Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyC, '\u0003', LeftCtrlPressed, keyDown: true);

        Assert.NotNull(key);
        Assert.Equal(ConsoleKey.C, key.Value.Key);
        Assert.True((key.Value.Modifiers & ConsoleModifiers.Control) != 0);
    }

    [Fact]
    public void Shift_And_Alt_Modifiers_Are_Reported()
    {
        var key = Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyA, 'A', ShiftPressed | LeftAltPressed, keyDown: true);

        Assert.NotNull(key);
        Assert.True((key.Value.Modifiers & ConsoleModifiers.Shift) != 0);
        Assert.True((key.Value.Modifiers & ConsoleModifiers.Alt) != 0);
    }

    [Fact]
    public void Key_Up_Is_Ignored()
    {
        Assert.Null(Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyA, 'a', 0, keyDown: false));
    }

    [Fact]
    public void Modifier_Only_Key_Is_Not_A_Key()
    {
        // 放行会让 Shift+'+' 的 Shift 按下吃掉 Ctrl+X 前缀(真实缺陷)
        Assert.Null(Dsh.Pty.WindowsConsoleRecord.MapKey(VirtualKeyShift, '\0', ShiftPressed, keyDown: true));
        Assert.Null(Dsh.Pty.WindowsConsoleRecord.MapKey(0x11, '\0', LeftCtrlPressed, keyDown: true));
        Assert.Null(Dsh.Pty.WindowsConsoleRecord.MapKey(0x12, '\0', LeftAltPressed, keyDown: true));
    }

    [Fact]
    public void Left_Button_Press_Maps_To_Button_Zero()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(5, 7, 0x0001, 0);

        Assert.NotNull(mouse);
        Assert.Equal(0, mouse.Value.Button);
        Assert.True(mouse.Value.Pressed);
        Assert.Equal(5, mouse.Value.X);
        Assert.Equal(7, mouse.Value.Y);
    }

    [Theory]
    [InlineData(0x0004u, 1)]
    [InlineData(0x0002u, 2)]
    public void Middle_And_Right_Buttons_Map_To_Sgr_Numbers(uint buttonState, int expected)
    {
        var mouse = WindowsConsoleInputReader.MapMouse(1, 1, buttonState, 0);

        Assert.NotNull(mouse);
        Assert.Equal(expected, mouse.Value.Button);
        Assert.True(mouse.Value.Pressed);
    }

    [Fact]
    public void Wheel_Up_Reports_Positive_Delta()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(2, 3, 1u << 16, MouseWheeled);

        Assert.NotNull(mouse);
        Assert.True(mouse.Value.IsWheel);
        Assert.Equal(1, mouse.Value.WheelDelta);
    }

    [Fact]
    public void Wheel_Down_Reports_Negative_Delta()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(2, 3, unchecked((uint)(-1 << 16)), MouseWheeled);

        Assert.NotNull(mouse);
        Assert.True(mouse.Value.IsWheel);
        Assert.Equal(-1, mouse.Value.WheelDelta);
    }

    [Fact]
    public void Pointer_Move_With_Held_Button_Is_A_Drag()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(2, 3, 0x0001, MouseMoved);

        Assert.NotNull(mouse);
        Assert.True(mouse.Value.IsMove);
        Assert.True(mouse.Value.IsDrag);
        Assert.True(mouse.Value.Pressed);
        Assert.Equal(0, mouse.Value.Button);
    }

    [Fact]
    public void Pointer_Move_Without_Button_Has_No_Button()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(2, 3, 0, MouseMoved);

        Assert.NotNull(mouse);
        Assert.True(mouse.Value.IsMove);
        Assert.False(mouse.Value.Pressed);
        Assert.Equal(3, mouse.Value.Button);
    }

    [Fact]
    public void Button_Release_Is_Not_A_Press()
    {
        var mouse = WindowsConsoleInputReader.MapMouse(2, 3, 0, 0);

        Assert.NotNull(mouse);
        Assert.False(mouse.Value.Pressed);
    }
}

