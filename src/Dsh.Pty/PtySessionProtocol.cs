namespace Dsh.Pty;

/**
 * 常驻会话与 proxy 之间的控制约定:
 *   - 会话(TUI 跑在 daemon 的 pty 上)在 Ctrl+X D 时向自己的 pty 写出 DetachMarker, 表示"只是让 proxy 离开, 我继续跑";
 *   - 标记用 OSC(私有编号 888), daemon 的 VtScreen 丢弃 OSC, 所以不会污染会话画面;
 *   - attach 侧的 proxy 在输出流里看到它即结束隧道返回(用户看到的是回到了自己的终端), 会话本身不受影响。
 * 另: 由 daemon 托管的 TUI 子进程会带上 ChildVariable 环境变量, 以便它知道自己就是常驻会话本身。
 */
public static class PtySessionProtocol
{
    public const string DetachMarker = "\u001b]888;dsh-detach\u0007";

    public const string ChildVariable = "DSH_PTY_CHILD";

    /** daemon 托管会话时给子进程注入的会话 id: 常驻 TUI 靠它找到自己的尺寸文件。 */
    public const string SessionVariable = "DSH_PTY_SESSION_ID";

    /**
     * daemon 托管会话时给子进程注入的 harness 持久化根。与 DSH_HOME 区分开: DSH_HOME 决定 daemon 的 run 根,
     * 这个只决定会话进程自己的 home, 因此单 daemon 也能托管属于不同 home 的会话。需与 Dsh.Boot.HarnessHome.ChildHomeEnv 一致。
     */
    public const string ChildHomeVariable = "DSH_HARNESS_HOME";
}
