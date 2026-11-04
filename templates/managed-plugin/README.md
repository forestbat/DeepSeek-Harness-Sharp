# 托管插件模板与作者指南

一个可被宿主发现的插件需要一个实现 `IDshPlugin` 的类、csproj 中的生成器 props import，以及放在宿主 `plugins/<目录名>/` 下的编译产物（主 dll + deps.json + 私有依赖）。本模板保留原有 `IDshPlugin`、`Apply(Context, object?)`、类型化依赖与配置约定。
目录名不参与识别：加载后一律以清单内的包名为准，目录名只做发现入口。手工摆放时建议按包名把 `/` 换成 `__`（如 `@scope__one`）便于人读；而 `/plugins add` 装出来的目录名是 dll 文件名（如 `MyPlugin`）——两种命名都能被正常发现。

本目录可直接复制为插件工程：`DshPluginTemplate.csproj` + `Plugin.cs`。模板通过 `SystemPrompt` 注册可配置的提示词段，并保留读文件、写文件和 HTTP 三个真实转发工具；工具使用宿主能力工厂，不直接访问文件系统或网络。

原生（AOT）插件使用相同操作定义，通过 C ABI v1 的可选宿主能力回调调用宿主；写法见 `templates/native-plugin/README.md`。

## 原有插件生命周期与宿主能力

`Inject` / `InjectTypes` 声明服务依赖；本模板依赖 `SystemPrompt`、`ToolRuntime` 和 `IPluginHostFactory`。`ConfigType` 声明 `TemplateConfig`，其 `Message` 从插件配置绑定；`Apply` 注册提示词与工具，返回的 `IDisposable` 在卸载时逆序撤销这些效果。原有接口没有替换，也没有新增兼容方法。

`ctx.Get<IPluginHostFactory>()!.Create(package, ctx)` 为原有 `IDshPlugin` 提供同一组宿主中介能力，包名应与程序集声明一致。模板保留完整 `Context` 组合能力，因此不是隔离 worker 插件；启用沙箱时必须显式授予相应制品宿主权限，不会自动回退到不受保护执行。使用宿主中介不等于使进程内任意代码成为受限代码。

- `host.RegisterTool(new PluginToolDefinition(name, description, parameters, execute))` 返回可释放的注册；`execute` 获得 JSON 参数及真实取消令牌。
- `await host.CallAsync(PluginHostOperations.ReadFile, arguments, cancellationToken)`：`{path, offset?, count?}` 返回 `{data, length, eof}`；`data` 为 base64。宿主限制单片大小。
- `PluginHostOperations.WriteFile`：`{path, data, append?}` 返回 `{bytesWritten}`；`data` 为 base64。对 settings 的写入也必须经过宿主审批 gate，不能直接写配置文件绕开审批。
- `PluginHostOperations.SendHttp`：`{url, method?, headers?, body?}` 返回 `{status, headers, body}`；请求和响应 `body` 都为 base64。宿主应用网络权限、代理配置及响应大小限额。
- `host.Log(level, message)` 通过宿主 logger 记录日志。

需要受限 worker 的插件同样实现 `IDshPlugin`，在 `Inject` / `InjectTypes` 中声明 `IPluginHostFactory`，并只依赖 worker 能提供的服务。
正常的 `Apply` / `ApplyAsync` 通过能力工厂创建 `IPluginHost`，`ConfigType` 按普通配置绑定，返回的 `IDisposable` 由运行时释放。
此时收到的是 worker 本地 `Context`，不能取得父进程的 `Context`、agent、服务对象或凭据，也不把透明 RPC 当作进程内 API。
自定义宿主使用宿主能力 SDK 或隔离 worker 时，引用 `Dsh.Plugins.Sandbox`，并在组合前调用已有的生成式 `DshPluginCatalog.RegisterAll()`，接入能力工厂与同一进程入口的 worker 模式。

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
- **跨程序集默认值**：嵌套配置类型在独立程序集时，用 `System.ComponentModel.DefaultValueAttribute` 声明生成 schema 所需的默认值；宿主生成器不会在运行时构造类型来推断默认值。
- **AOT 边界**：AOT 编译的插件是原生库，走原生通道（契约是 C ABI），类型化注入与托管配置绑定不适用；托管通道内本设计无代码生成、无动态泛型，裁剪安全。

## 共享依赖（库型插件）

判据只有一条：**该程序集的类型是否跨插件边界流动**（作为服务返回值、事件载荷、共享基类流经 `ctx.Provide/Get` 或会话事件）。

- 是 → 共享库自身与每个下游插件都声明 `[assembly: DshSharedDependency("程序集名")]`；该程序集不拷进任何插件目录，由宿主放进 `plugins/.shared/` 全进程加载一次。
- 否（纯内部实现）→ 各插件目录私有一份即可，不要声明共享。

同名不同版本：semver 兼容取最高者并 WARN；主版本不兼容拒绝后装者并列出冲突双方。不允许并排加载——类型分裂的 bug 比加载失败难查得多。

## 安装与卸载

- `/plugins add <路径>`：把目标 dll 及其 deps.json 闭包拷入 `plugins/<dll 文件名>/`（宿主镜像已有的程序集自动跳过），随后热装载。
- `/plugins remove <包名>`：协作式卸载；仍有下游 Inject 其服务的共享库会被拒绝并列出依赖方。
- 更新插件：先 `/plugins remove <包名>`（卸载效果、释放文件占用，并删除 `/plugins add` 落盘的 `plugins/<目录名>/`，目录名就是 dll 文件名；手工摆放的目录不归 add 管，remove 只做摘除，需你自己替换文件），然后 `/plugins add <新构建的 dll>`。同名文件内容不一致时 add 一律拒绝——这是为了不覆盖手放或他人写的同名插件，所以更新必须先 `remove` 旧副本（或手工删掉旧文件）。
- 工具重名不再是错误：后注册者自动以 `<包名末段>-<工具名>` 可见并 WARN；PTC 模式下非标识符工具名用 `tools.call("<name>", args)` 调用。
