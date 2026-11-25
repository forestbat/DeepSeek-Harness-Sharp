namespace Dsh.Tui;

/**
 * 终端输入协议的开关: 默认情况下终端把 Shift+Enter 与 Enter 送成同一个字节(\r), 只有让终端按
 * kitty 键盘协议或 xterm modifyOtherKeys 编码, 带修饰键的按键才可区分 —— 这是"Shift+Enter 换行"的前提。
 *
 * 两种协议都开: 终端只认自己实现的那一种, 而解码侧两种序列都认(见 TerminalInputParser.CsiUKey / TryModifyOtherKeys)。
 * 退出时按相反顺序复位, 不给宿主终端留下改过的键盘模式。
 */
internal static class TerminalInputModes
{
    /** kitty 键盘协议 flag 1(disambiguate escape codes): Enter/Esc/Tab 与 Ctrl/Alt 组合按 CSI-u 上报。 */
    private const string KittyDisambiguateEnable = "\u001b[>1u";

    private const string KittyDisambiguateDisable = "\u001b[<u";

    /** xterm modifyOtherKeys 级别 2: 带修饰键的按键按 `ESC[27;修饰;码点~` 上报。 */
    private const string ModifyOtherKeysEnable = "\u001b[>4;2m";

    private const string ModifyOtherKeysDisable = "\u001b[>4;0m";

    public static string Enable => KittyDisambiguateEnable + ModifyOtherKeysEnable;

    public static string Disable => ModifyOtherKeysDisable + KittyDisambiguateDisable;
}
