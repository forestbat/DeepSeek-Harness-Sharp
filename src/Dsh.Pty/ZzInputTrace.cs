namespace Dsh.Pty;

/** 临时诊断(用完删除): 把输入的每一跳落到当前工作目录下的 zz-input-trace.log。 */
public static class ZzInputTrace
{
    private static readonly object Gate = new();

    public static void Log(string stage, string message) => Log(stage, message, 0);

    public static void Log(string stage, string message, int _)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {stage} {message}\n";
            lock (Gate)
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "zz-input-trace.log"), line);
        }
        catch (Exception)
        {
        }
    }

    public static void Log(string stage, byte[] bytes, int count)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {stage} {count}: {Convert.ToHexString(bytes.AsSpan(0, count))}\n";
            lock (Gate)
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "zz-input-trace.log"), line);
        }
        catch (Exception)
        {
        }
    }
}
