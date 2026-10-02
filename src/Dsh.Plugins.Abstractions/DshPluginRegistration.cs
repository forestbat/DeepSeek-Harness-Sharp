namespace Dsh.Plugins;

/** 生成目录导出的插件登记项:宿主据此直接构造插件实例,无需反射遍历插件类型。 */
public sealed record DshPluginRegistration(
    string Package,
    int DescriptorVersion,
    Func<IDshPlugin> Create,
    string? Entry = null);

/** 声明插件类的入口名(如 tui/gui);该类需实现宿主的入口契约。 */
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DshEntrypointAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/** 标注汇编级初始化方法(无参静态 void):生成的 DshPluginBootstrap 在启动时显式调用,
 *  取代 [ModuleInitializer] 隐式自注册——裁剪/AOT 下更可控,也不需要全局 NoWarn。 */
[AttributeUsage(AttributeTargets.Method)]
public sealed class DshPluginInitializerAttribute : Attribute;

/** 声明一个程序集级共享依赖:该程序集的类型会跨插件边界流动(服务契约/事件载荷),
 *  加载器把它解析进全进程唯一的共享上下文,而不是插件目录里的私有副本。 */
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DshSharedDependencyAttribute(string assemblyName) : Attribute
{
    public string AssemblyName { get; } = assemblyName;
}
