using Dsh.Runtime;

namespace Dsh.Plugins;

public interface IDshPlugin
{
    string[] Inject { get; }

    /** 类型化依赖边:按契约类型声明依赖,宿主在服务表里按可赋值性匹配唯一激活服务。
     *  多个服务可赋给同一类型时该边按不满足处理,改用 Inject 字符串按名消歧。 */
    Type[] InjectTypes => [];

    /** 插件自带配置类型:声明后宿主把 parameters 段绑定成该类型再交给 Apply,未知字段在激活期 WARN。
     *  类型须有公共无参构造(带默认值的 record 即可);null 表示沿用无类型字典。 */
    Type? ConfigType => null;

    IDisposable Apply(Context ctx, object? config);
}

/** 一个被杀掉/跳过/装载失败的插件制品与原因,供宿主逐文件 WARN、/plugins list 失败分组与 doctor。 */
public sealed record PluginSkip(string File, string Reason);

public interface IPluginManager
{
    IReadOnlyList<string> PackageNames { get; }

    /** 启动扫描期间逐文件的跳过/失败清单(含原生装载失败的结构化原因)。 */
    IReadOnlyList<PluginSkip> LoadFailures { get; }

    string Describe(string package);

    Task<string> AddAsync(string packageOrPath);

    Task<string> RemoveAsync(string package, bool force = false);

    Task<string> DisableAsync(string package);

    Task<string> EnableAsync(string package);
}
