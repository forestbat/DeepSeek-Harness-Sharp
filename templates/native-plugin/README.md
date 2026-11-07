# 原生（AOT）插件作者指南

本目录只有文档，没有模板工程：原生插件没有需要复制的骨架工程，**样例是 `tests/Dsh.NativePluginSample/`**（`Dsh.NativePluginSample.csproj` + `SamplePlugin.cs` 两个文件，直接复制改造即可）。
托管插件的写法见 `templates/managed-plugin/README.md`；两者的差别见本文末尾的对照表。

## 一句话定位

原生插件是一个 **NativeAOT 编译出来的共享库**（`.so`/`.dylib`/`.dll`），与宿主通过 **C ABI v1** 交换日志、工具、配置及可选的宿主能力调用。
它看不到父进程的服务表、类型或会话。
文件和网络操作与托管 `IDshPlugin` 通过 `IPluginHostFactory` 取得的能力使用同一组 `Dsh.Plugins.Abstractions` 操作常量，不把完整 Context 伪装成远程对象。
原有 v1 二进制可以继续加载、配置和调用；新回调不要求旧插件重新编译。

## 最小骨架

csproj（`tests/Dsh.NativePluginSample/Dsh.NativePluginSample.csproj` 即范例）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- 引入生成器：托管清单与原生 ABI 胶水都由它产出 -->
  <Import Project="..\..\src\Dsh.Plugins.Generator\DshPluginPackage.props" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- ABI 函数表用函数指针，必须开 unsafe -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- 编成原生共享库；一个 RID 一份产物 -->
    <PublishAot>true</PublishAot>
    <NativeLib>Shared</NativeLib>
    <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <!-- 只引用契约与助手；不要引用宿主其它模块 -->
    <ProjectReference Include="..\..\src\Dsh.Plugins.Native\Dsh.Plugins.Native.csproj" />
  </ItemGroup>
</Project>
```

插件类（只声明包名与工具集合）：

```csharp
public sealed class SamplePlugin : IDshNativePlugin
{
    public string Package => "sample.native-echo";

    public IReadOnlyList<NativePluginTool> Tools =>
    [
        new("native_echo", "Echo the given message back.", EchoSchema, Echo),
    ];
}
```

## 生成器已经替你做掉的（不要手写）

- 保留 `dsh_plugin_package` 与原有 `dsh_plugin_entry(host, plugin)` C 符号，另导出可选的 `dsh_plugin_entry_with_host_size(host, hostApiSize, plugin)`；
- NUL 结尾的 UTF-8 编解码、句柄表与缓冲交换；当前宿主一次提供完整协议帧，不为取回结果再次执行工具；
- 工具注册与调用派发，以及两侧的异常拦截（异常不会穿过 ABI 边界）；
- 日志、配置、工具表和 `DshNativePluginRuntime.CallHost` 的激活／撤销。
你只声明 `Package` 与 `Tools`；手写导出会与生成物重复定义。

## 除胶水之外，必须自己注意的

**构建与分发**
- `PublishAot` + `NativeLib=Shared` + **具体 RID**：一个平台/架构一份产物，跨平台要各编一份（改 `RuntimeIdentifier` 或按 RID 分别发布）。
- 作者工程只需直接引用 `Dsh.Plugins.Native`。能力 DTO 来自它引用的 `Dsh.Plugins.Abstractions`，后者还引用 `Dsh.Runtime`，因此不能把整个传递依赖闭包描述为仅含 BCL。原生调用仍只交换 C 函数表、UTF-8 与 JSON，不向插件提供宿主 Context；不要额外引用 `Dsh.Core`、宿主配置或 YAML 模块。
- NativeAOT 把可达的托管依赖编入原生映像，分发共享库时不依赖托管 `deps.json` 或运行期 DLL 装载闭包；**额外的原生依赖要靠操作系统解析**（插件目录不会自动加进搜索路径，必要时用 `$ORIGIN`/RPATH 或放进系统库路径）。
- 插件自身也是 AOT 代码：不用反射与动态代码；JSON 用 `JsonDocument`/`JsonNode`（样例如此）或源生成上下文，不要无上下文地 `JsonSerializer.Serialize<T>`。

**能力面（ABI v1 的可选尾部）**
- 没有 `Inject`/服务提供、没有 `ConfigType` 配置绑定、没有提示词段、没有会话事件、没有 `[DshEntrypoint]`；`DshNativePluginRuntime.Config` 返回本插件 JSON 配置。
- 工具可通过 `DshNativePluginRuntime.RegisterTool(name, description, parametersJson, invoke)` 登记、通过 `UnregisterTool(handle)` 撤销，宿主侧即时生效。新宿主优先调用带大小的入口；旧宿主调用原入口时，新插件只读取原始函数表前缀，仍能使用日志、工具及配置，不能调用缺失的能力回调。
- `DshNativePluginRuntime.CallHost(PluginHostOperations.ReadFile, inputJson)` 返回 JSON 文本：`{path, offset?, count?}` → `{data, length, eof}`，`data` 为 base64。
- `PluginHostOperations.WriteFile`：`{path, data, append?}` → `{bytesWritten}`，`data` 为 base64；写 settings 同样受宿主审批 gate 约束。
- `PluginHostOperations.SendHttp`：`{url, method?, headers?, body?}` → `{status, headers, body}`；请求和响应 `body` 为 base64。网络权限、代理及响应限额由共同宿主实现。
- `CallHost` 必须处于激活期；操作拒绝／取消／失败以异常传回，不返回空对象冒充成功。样例的 `native_read_file`、`native_write_file`、`native_http` 是这三条真实调用路径。

**缓冲与副作用**
- `host_call` 的参数为 context、NUL 结尾的 operation/arguments、output、capacity。返回 UTF-8 字节长度，负数表示协议失败；内部响应为 `{"result": ...}` 或 `{"error": "..."}`，公开 `CallHost` 负责解包并传播错误。
- output 必须指向调用者实际拥有的至少一个完整协议帧（1 MiB）缓冲；空指针或短缓冲在执行操作前直接拒绝，不支持探测长度、保留回复或重试。SDK 每次只调用一次；超出帧大小的回复明确失败，不自动重放可能已发生的写入或网络请求。
- 宿主只在调用期间写入 output，从不持有该指针；成功返回写入长度，调用者只能读取这部分字节。配置沿用原 v1 的只读长度探测；插件侧仅为旧宿主保留原 v1 工具结果重取语义。
- 原生调用是同步的，ABI 没有独立取消令牌；宿主可通过其工具调用和 worker 生命周期取消真实操作。不要启动不受插件生命周期管理的回调线程。

**工具契约**
- 名字/描述/JSON Schema 都是字符串：Schema **必须能被解析**，否则激活失败（不是工具失败）。
- 调用是 JSON 进、JSON 出；返回值必须是 JSON 对象，其中 `{"text": "..."}` 才会作为文本展示给模型，否则原样输出 JSON；返回 null 或抛错都算工具失败。
- 工具名沿用与托管插件相同的命名规则（合法标识符才能在 PTC 里直接调用；重名由宿主自动改名并 WARN）。
- 当前桥接逐插件序列化同步工具调用，并只在实际调用线程上传递该次真实工具上下文；初始化和后台线程没有工具审批身份。插件自己的后台状态仍需自行保证线程安全。

**线程与生命周期**
- `Deactivate` 停止工具与回调；插件必须在这里停掉后台线程/定时器，此后再调用宿主回调即属未定义行为。NativeAOT 不支持 `dlclose`/`FreeLibrary`，共享库映像保留至承载进程退出。
- 日志统一走 `DshNativePluginRuntime.Log(level, message)`（激活前是静默的），不要写控制台。

**握手失败不再静默**
- 缺少导出符号（`dsh_plugin_package`/`dsh_plugin_entry`）、ABI 版本不匹配、库不是 `NativeLib=Shared` 产物时，装载失败会给出**结构化原因**：启动对 `plugins/` 下的候选逐文件记 WARN，`/plugins list` 增「加载失败」分组，`/plugins doctor` 打印完整原因。
- 因此"库放进 `plugins/` 却什么都没发生"时，直接 `/plugins doctor`；原因会按顺序标注是缺导出、版本不符、非共享库还是 quarantine。
- 对照：托管插件缺清单同样不再静默（见托管指南）。

**装载与更新**
- 主要分发方式是**放进 `<安装目录>/plugins/<目录>/` 启动加载**（原生库与托管插件同目录规则：目录名不参与识别）。
- `/plugins add <路径>` 现在**支持原生插件**：按扩展名（`.so`/`.dylib`，Windows 下的原生 `.dll`）与导出符号识别原生库，路由到原生装载；AOT 宿主不能热载托管 dll，但加载原生库可行。
- 更新与托管插件同规矩：**同名文件内容不一致不覆盖**。先 `/plugins remove <包名>`（或直接停宿主）→ 删掉旧目录 → 放入新库 → 重启。
- 原生插件停用会撤销工具和回调，映像随宿主或 worker 进程退出回收，没有可回收 ALC，也没有托管那种“回收校验/leaked”标记。

**稳定性与进程边界**
- 每个原生插件拥有 `NativePluginLoadContext`；工具运行失败会向调用者返回错误，原生故障按既有 guard、Faulted 与 quarantine 路径处理。
- 这些进程内防护不等于 OS 权限隔离。共享 worker 中的插件互相信任同一进程内存，broker 仍逐插件检查权限，但不能声称同进程恶意代码无法冒用其他插件身份。显式进程内插件拥有宿主进程权限，不能声称已被沙箱保护。

## 编译期诊断

| 诊断码 | 级别 | 含义与处理 |
| --- | --- | --- |
| DSHABI001 | Error | 一个程序集有多个 `IDshNativePlugin` 实现。一个程序集只能有一个。 |
| DSHABI002 | Error | ABI 协议结构体缺少某个角色字段（改动协议本身时才会遇到）。 |
| DSHABI003 | Warning | 协议里出现了生成器不认识的角色，该函数不会进入函数表。 |

## 与托管插件的对照

| 维度 | 托管（`templates/managed-plugin`） | 原生（本文） |
| --- | --- | --- |
| 产物 | 托管 dll + deps.json 闭包 | 每 RID 一份原生共享库 |
| 契约 | `IDshPlugin`；worker 使用本地 `Context` 与 `IPluginHostFactory` | C ABI v1 + 可选带大小入口 |
| 能力面 | 显式工具/日志/文件/网络；完全信任接口另有服务组合 | 显式工具/日志/配置/文件/网络 |
| 装载 | 启动扫描 + `/plugins add` 热装载托管 dll | 启动扫描 + `/plugins add` 热装载原生库 |
| 卸载 | 协作式卸载 + 可回收 ALC（有 leaked 校验） | `Deactivate`；映像随承载进程退出 |
| 版本闸门 | 清单 `DescriptorVersion` | ABI 版本（不符即结构化失败，`/plugins doctor` 可查） |
| 故障隔离 | OS worker 才是权限边界；ALC 不是安全边界 | OS worker 才是权限边界；进程内 guard 不是沙箱 |
| AOT 宿主 | 不能在运行期装载 | 正常加载 |

