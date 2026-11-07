namespace Dsh.Boot;

/**
 * settings.yaml 的写入守卫: 只回答一个问题 —— 上一次 Capture 之后文件变了没有, 变了还能不能解析。
 * 因此它不认识任何工具、通知或会话; 触发时机由调用方决定(Dsh.Inspection 用工具通知触发, 别的宿主也可以不用)。
 * 坏文本就地还原成上一次通过校验的文本, 原文隔离保存, 说明文本交回给调用方去告诉用户或模型。
 */
public sealed class SettingsFileGuard(HarnessHome home)
{
    private readonly Lock _gate = new();
    private (DateTime Write, long Length) _stamp;
    private string? _good;

    /** 写之前调用: 记下文件戳, 只有真的变了才读内容。 */
    public void Capture()
    {
        lock (_gate)
            _stamp = Stamp();
    }

    /** 启动时调用: 刚成功解析过的文本就是基线。 */
    public void Accept()
    {
        lock (_gate)
        {
            _stamp = Stamp();
            if (TryRead(out var text) && HarnessSettings.TryValidate(text, out _))
                _good = text;
        }
    }

    /** 文件在这段时间被改过就校验一次; 坏文本立刻还原并返回给调用方的说明。 */
    public string? Check()
    {
        lock (_gate)
        {
            var stamp = Stamp();
            if (stamp == _stamp)
                return null;
            _stamp = stamp;
            if (!TryRead(out var text))
                return null;
            if (HarnessSettings.TryValidate(text, out var problem))
            {
                _good = text;
                return null;
            }
            if (_good is null)
                return null;
            var quarantined = HarnessSettings.Quarantine(home, text);
            HarnessSettings.Restore(home, _good);
            return $"settings.yaml 的这次写入已被撤销: {problem}。原文保存为 {quarantined}。改配置请用 settings_set, 不要直接改文件。";
        }
    }

    private (DateTime Write, long Length) Stamp()
    {
        var file = new FileInfo(home.SettingsFile);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default, 0);
    }

    private bool TryRead(out string text)
    {
        text = "";
        try
        {
            if (!File.Exists(home.SettingsFile))
                return false;
            text = File.ReadAllText(home.SettingsFile);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
