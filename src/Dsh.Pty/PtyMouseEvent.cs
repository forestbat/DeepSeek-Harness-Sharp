namespace Dsh.Pty;

/** 从宿主终端报文解析出的鼠标事件; 字段与 Win32 MOUSE_EVENT_RECORD 对齐, daemon 原样注入子进程控制台。 */
public readonly record struct PtyMouseEvent(short X, short Y, uint ButtonState, uint EventFlags);
