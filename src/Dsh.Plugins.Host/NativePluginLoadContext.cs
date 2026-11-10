using Dsh.Plugins.Generated;

namespace Dsh.Plugins;

/** 原生插件的运行期故障状态:Loaded → Active;一旦故障为 Faulted,此后拒绝再调用其工具。 */
public enum NativePluginState
{
    Loaded = 0,
    Active = 1,
    Faulted = 2,
    Unloaded = 3,
}

/** 原生插件故障计数与禁装表:按库文件路径记账,同一插件累计到 BanThreshold 即加入 quarantine。
 *  进程级单例——故障消息在同一进程内跨重复装载累积。 */
public sealed class NativePluginQuarantine
{
    /** 同类故障再次出现即禁装的阈值。 */
    public const int BanThreshold = 2;

    public static NativePluginQuarantine Default { get; } = new();

    private readonly Dictionary<string, int> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();

    public bool IsBanned(string key)
    {
        lock (_sync)
            return _faults.TryGetValue(key, out var count) && count >= BanThreshold;
    }

    public int FaultCount(string key)
    {
        lock (_sync)
            return _faults.GetValueOrDefault(key);
    }

    /** 记一次故障并返回累计次数;不在此处移除,禁装判定由 IsBanned 给出。 */
    public int RecordFault(string key)
    {
        lock (_sync)
        {
            var count = _faults.GetValueOrDefault(key) + 1;
            _faults[key] = count;
            return count;
        }
    }

    public void Reset(string key)
    {
        lock (_sync)
            _faults.Remove(key);
    }
}

/** Owns logical native-plugin activation, calls, and fault state.
 * Native image memory is reclaimed with its owning process, not by logical disposal. */
public sealed class NativePluginLoadContext : IDisposable
{
    private readonly DshPluginApiBinding _binding;
    private readonly Lock _sync = new();
    private readonly HashSet<int> _inFlight = [];
    private NativePluginState _state = NativePluginState.Loaded;

    internal NativePluginLoadContext(DshPluginApiBinding binding, string sourcePath, string package, string settingsSchemaJson = "")
    {
        _binding = binding;
        SourcePath = sourcePath;
        Package = package;
        SettingsSchemaJson = settingsSchemaJson;
    }

    public string Package { get; }

    public string SourcePath { get; }

    /** 插件在 dsh_plugin_settings_schema 里声明的配置面(JSON); 未声明为空串。 */
    public string SettingsSchemaJson { get; }

    public NativePluginState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public bool IsFaulted => State == NativePluginState.Faulted;

    /** 进入一次插件调用:已故障/已卸载则拒绝;否则登记线程,供故障遏制判断在飞调用。 */
    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_state is NativePluginState.Faulted or NativePluginState.Unloaded)
                return false;
            _inFlight.Add(Environment.CurrentManagedThreadId);
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
            _inFlight.Remove(Environment.CurrentManagedThreadId);
    }

    public int InFlightCount
    {
        get
        {
            lock (_sync)
                return _inFlight.Count;
        }
    }

    public void MarkActive()
    {
        lock (_sync)
        {
            if (_state == NativePluginState.Loaded)
                _state = NativePluginState.Active;
        }
    }

    /** 记录一次故障:标记 Faulted 并累计 quarantine。写日志交给调用方(它持有 logger)。不在此处 Free。 */
    public int RecordFault(string reason)
    {
        lock (_sync)
        {
            if (_state is NativePluginState.Faulted or NativePluginState.Unloaded)
                return NativePluginQuarantine.Default.FaultCount(SourcePath);
            _state = NativePluginState.Faulted;
        }
        var count = NativePluginQuarantine.Default.RecordFault(SourcePath);
        return count;
    }

    public int Activate(nint context) => _binding.Activate(context);

    public void Deactivate(nint context) => _binding.Deactivate(context);

    public string? InvokeTool(nint context, int handle, string inputJson) => _binding.InvokeTool(context, handle, inputJson);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_state == NativePluginState.Unloaded)
                return;
            _state = NativePluginState.Unloaded;
        }
    }
}
