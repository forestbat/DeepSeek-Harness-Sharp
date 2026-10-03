using Dsh.Tui;

namespace Dsh.Tests;

/** 测试用 VT 解释器: 把 AnsiRenderer 的输出(和真实终端输出)解析成字符屏, 供文案级断言与出图。 */
internal sealed class VirtualTerminal
{
        private readonly bool[,] _wideRightHalf;

        public VirtualTerminal(int width, int height)
        {
            Width = width;
            Height = height;
            Screen = new char[width, height];
            _wideRightHalf = new bool[width, height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    Screen[x, y] = ' ';
        }

        public int Width { get; }

        public int Height { get; }

        public char[,] Screen { get; }

        private int CursorX { get; set; }

        private int CursorY { get; set; }

        private bool WrapPending { get; set; }

        public void Feed(string output)
        {
            var index = 0;
            while (index < output.Length)
            {
                var character = output[index];
                if (character == '\x1b')
                {
                    index = ConsumeEscape(output, index);
                    continue;
                }
                if (character == '\r')
                {
                    CursorX = 0;
                    WrapPending = false;
                    index++;
                    continue;
                }
                if (character == '\n')
                {
                    CursorY = Math.Min(Height - 1, CursorY + 1);
                    WrapPending = false;
                    index++;
                    continue;
                }
                Put(character);
                index++;
            }
        }

        private int ConsumeEscape(string output, int index)
        {
            var cursor = index + 1;
            if (cursor >= output.Length || output[cursor] != '[')
                return cursor;
            cursor++;
            var start = cursor;
            while (cursor < output.Length && !char.IsLetter(output[cursor]))
                cursor++;
            if (cursor >= output.Length)
                return cursor;
            var final = output[cursor];
            var parameters = output[start..cursor];
            cursor++;
            switch (final)
            {
                case 'H':
                    {
                        var parts = parameters.Split(';');
                        var row = parts.Length > 0 && int.TryParse(parts[0], out var parsedRow) ? parsedRow : 1;
                        var column = parts.Length > 1 && int.TryParse(parts[1], out var parsedColumn) ? parsedColumn : 1;
                        CursorY = Math.Clamp(row - 1, 0, Height - 1);
                        CursorX = Math.Clamp(column - 1, 0, Width - 1);
                        WrapPending = false;
                        break;
                    }
                case 'J':
                    if (parameters is "" or "2")
                    {
                        for (var y = 0; y < Height; y++)
                            for (var x = 0; x < Width; x++)
                            {
                                Screen[x, y] = ' ';
                                _wideRightHalf[x, y] = false;
                            }
                        CursorX = 0;
                        CursorY = 0;
                    }
                    break;
                case 'K':
                    for (var x = CursorX; x < Width; x++)
                    {
                        Screen[x, CursorY] = ' ';
                        _wideRightHalf[x, CursorY] = false;
                    }
                    break;
                case 'C':
                    // 光标右移(应用用 \e[67C 跳到右侧面板列)
                    CursorX = Math.Clamp(CursorX + ParseCount(parameters, 1), 0, Width - 1);
                    WrapPending = false;
                    break;
                case 'X':
                    // 擦除字符(应用用 \e[67X 清掉面板左侧区域)
                    for (var x = CursorX; x < Math.Min(Width, CursorX + ParseCount(parameters, 1)); x++)
                    {
                        Screen[x, CursorY] = ' ';
                        _wideRightHalf[x, CursorY] = false;
                    }
                    break;
            }
            return cursor;
        }

        private static int ParseCount(string parameters, int fallback)
            => int.TryParse(parameters, out var parsed) && parsed > 0 ? parsed : fallback;

        private void Put(char character)
        {
            if (WrapPending)
            {
                CursorX = 0;
                CursorY = Math.Min(Height - 1, CursorY + 1);
                WrapPending = false;
            }
            if (_wideRightHalf[CursorX, CursorY] && CursorX > 0)
                Screen[CursorX - 1, CursorY] = ' ';
            // 覆盖宽字符左半时右半一并失效(真实终端行为), 否则残留的右半标记会把下一次写入误判为覆盖右半
            if (CursorX + 1 < Width && _wideRightHalf[CursorX + 1, CursorY])
            {
                Screen[CursorX + 1, CursorY] = ' ';
                _wideRightHalf[CursorX + 1, CursorY] = false;
            }
            Screen[CursorX, CursorY] = character;
            _wideRightHalf[CursorX, CursorY] = false;
            var advance = TerminalTextWidth.IsWide(character) ? 2 : 1;
            if (advance == 2 && CursorX + 1 < Width)
            {
                Screen[CursorX + 1, CursorY] = ' ';
                _wideRightHalf[CursorX + 1, CursorY] = true;
            }
            CursorX += advance;
            if (CursorX >= Width)
            {
                CursorX = Width - 1;
                WrapPending = true;
            }
        }
    }
