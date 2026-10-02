# 托管插件模板与作者指南

一个可被宿主发现的插件只需要三件事：一个实现 `IDshPlugin` 的类，csproj 里 import 生成器 props，编译产物（主 dll + deps.json + 私有依赖）放进宿主 `plugins/<目录名>/` 下。
目录名约定为包名把 `/` 换成 `__`；加载后以清单内的包名为准，目录名只做发现入口。

本目录即模板工程：`DshPluginTemplate.csproj` + `Plugin.cs`，每个文件的注释就是规则原文（做什么 + 为什么），可直接作为创造模式搭插件骨架的上下文。

## 编译期诊断

| 诊断码 | 级别 | 含义与处理 |
| --- | --- | --- |
| DSHPLUGIN001 | Error | 一个程序集有多个 `IDshPlugin` 实现。只留一个；要多包共用一个程序集，改用 `(string packageName)` 构造函数分派。 |
| DSHPLUGIN002 | Error | 插件类缺少公共无参或公共 `(string packageName)` 构造函数。生成清单要直接 new，别的形状不支持。 |
| DSHPLUGIN003 | Warning | `[DshEntrypoint]` 的名称为空或无效。入口名是宿主选主循环的键（如 `tui`），必须是非空字符串。 |
| DSHPLUGIN004 | Error | `[DshPluginInitializer]` 标在了不合规的方法上。初始化方法必须是无参静态 void。 |
| DSHPLUGIN005 | Warning | 程序集有插件实现但没有任何包名来源且程序集名不可用。补 `[assembly: DshPlugin("包名")]`。 |

## 多包模式

一个程序集要注册多个包（如主插件 + 附属工具包）：

```csharp
[assembly: DshPlugin("@scope/one")]
[assembly: DshPlugin("@scope/two")]

public sealed class Plugin(string packageName) : IDshPlugin
{
    public string[] Inject => packageName switch
    {
        "@scope/one" => [SystemPrompt.ServiceName],
        _ => [],
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        "@scope/one" => ApplyOne(ctx),
        _ => ApplyTwo(ctx),
    };
}
```

生成器为每个包名各生成一条登记，运行期各 new 一次、各自走激活生命周期。

## 类型化注入与配置

- **依赖声明优先用类型**：`InjectTypes => [typeof(IMyContract)]` 替代 `Inject` 字符串；宿主在服务表里按可赋值性找唯一激活服务，类型拼错在编译期就报。同一契约有多个实现时该类型边按不满足处理（pending WARN 会列出候选），改用 `Inject` 字符串按名消歧；`ctx.Get<T>()` 同理，歧义时抛错并列出可选名字。
- **契约类型必须跨插件同一份**：契约接口放进共享依赖程序集（`[assembly: DshSharedDependency]`），否则提供方与消费方各持一份类型身份，按类型匹配永远落空。
- **配置用自带类型**：声明 `ConfigType => typeof(MyConfig)` 后，`plugins.<包名>` 的参数段在 Apply 前绑定成该类型；未知字段（拼错）与类型错配在激活期 WARN 并列出已知字段，不再静默吞掉。配置类型须有公共无参构造。
- **AOT 边界**：AOT 编译的插件是原生库，走原生通道（契约是 C ABI），类型化注入与托管配置绑定不适用；托管通道内本设计无代码生成、无动态泛型，裁剪安全。

## 共享依赖（库型插件）

判据只有一条：**该程序集的类型是否跨插件边界流动**（作为服务返回值、事件载荷、共享基类流经 `ctx.Provide/Get` 或会话事件）。

- 是 → 共享库自身与每个下游插件都声明 `[assembly: DshSharedDependency("程序集名")]`；该程序集不拷进任何插件目录，由宿主放进 `plugins/.shared/` 全进程加载一次。
- 否（纯内部实现）→ 各插件目录私有一份即可，不要声明共享。

同名不同版本：semver 兼容取最高者并 WARN；主版本不兼容拒绝后装者并列出冲突双方。不允许并排加载——类型分裂的 bug 比加载失败难查得多。

## 安装与卸载

- `/plugins add <路径>`：把目标 dll 及其 deps.json 闭包拷入 `plugins/<目录名>/`（宿主镜像已有的程序集自动跳过），随后热装载。
- `/plugins remove <包名>`：协作式卸载；仍有下游 Inject 其服务的共享库会被拒绝并列出依赖方。
- 工具重名不再是错误：后注册者自动以 `<包名末段>-<工具名>` 可见并 WARN；PTC 模式下非标识符工具名用 `tools.call("<name>", args)` 调用。
