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

public interface IPluginManager
{
    IReadOnlyList<string> PackageNames { get; }

    string Describe(string package);

    Task<string> AddAsync(string packageOrPath);

    Task<string> RemoveAsync(string package, bool force = false);

    Task<string> DisableAsync(string package);

    Task<string> EnableAsync(string package);
}
