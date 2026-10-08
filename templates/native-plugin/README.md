# 原生（AOT）插件作者指南

本目录只有文档，没有模板工程：原生插件没有需要复制的骨架工程，**样例是 `tests/Dsh.NativePluginSample/`**（`Dsh.NativePluginSample.csproj` + `SamplePlugin.cs` 两个文件，直接复制改造即可）。
托管插件的写法见 `templates/managed-plugin/README.md`；两者的差别见本文末尾的对照表。

## 一句话定位

原生插件是一个 **NativeAOT 编译出来的共享库**（`.so`/`.dylib`/`.dll`），与宿主之间只有一条 **C ABI v1** 契约：**日志 + 工具**。
它跑在自己的原生镜像里，看不到宿主的服务表、类型与会话——想提供/消费服务、配置、提示词段、入口点的插件必须走托管通道。

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

- 导出 `dsh_plugin_package` 与 `dsh_plugin_entry` 两个 C 符号与握手；
- 句柄表、两段式缓冲协议（先问长度再写内容）、UTF-8 编解码；
- 工具注册与调用派发，以及两侧的异常拦截（异常不会穿过 ABI 边界）；
- 日志 sink 安装（`DshNativePluginRuntime.LogSink`）。
你只声明 `Package` 与 `Tools`；手写导出会与生成物重复定义。

## 除胶水之外，必须自己注意的

**构建与分发**
- `PublishAot` + `NativeLib=Shared` + **具体 RID**：一个平台/架构一份产物，跨平台要各编一份（改 `RuntimeIdentifier` 或按 RID 分别发布）。
- 只依赖 `Dsh.Plugins.Native`。宿主其它模块（`Dsh.Core`、`Dsh.Runtime` 等）不属于原生契约，依赖它们等于把宿主实现细节编进插件。
- 不需要 `deps.json`，也没有托管依赖闭包；**额外的原生依赖要靠操作系统解析**（插件目录不会自动加进搜索路径，必要时用 `$ORIGIN`/RPATH 或放进系统库路径）。
- 插件自身也是 AOT 代码：不用反射与动态代码；JSON 用 `JsonDocument`/`JsonNode`（样例如此）或源生成上下文，不要无上下文地 `JsonSerializer.Serialize<T>`。

**能力面（ABI v1 只有日志与工具）**
- 没有 `Inject`/服务提供、没有 `ConfigType` 配置绑定、没有提示词段、没有会话事件、没有 `[DshEntrypoint]`。
- 工具集合不再定死：除 `Activate` 时遍历一次 `Tools` 外，可在运行期增删——
  `DshNativePluginRuntime.RegisterTool(name, description, parametersJson, invoke)` 返回句柄，
  `DshNativePluginRuntime.UnregisterTool(handle)` 撤销；宿主侧 `ToolRuntime` 即时生效。
  反注册走 ABI v1 的**可选回调** `unregister_tool`：旧宿主该指针为 null，`UnregisterTool` 返回 false（工具无法撤销），
  插件应据此降级；`dsh_plugin_abi_version` 是独立的可选导出，供宿主在握手前精确区分「版本不符」与「其它拒绝」。

**工具契约**
- 名字/描述/JSON Schema 都是字符串：Schema **必须能被解析**，否则激活失败（不是工具失败）。
- 调用是 JSON 进、JSON 出；返回值必须是 JSON 对象，其中 `{"text": "..."}` 才会作为文本展示给模型，否则原样输出 JSON；返回 null 或抛错都算工具失败。
- 工具名沿用与托管插件相同的命名规则（合法标识符才能在 PTC 里直接调用；重名由宿主自动改名并 WARN）。
- `Invoke` **可能被并发调用**（宿主并行调度工具时），要自己保证线程安全。

**线程与生命周期**
- `Deactivate` 之后共享库会被 `NativeLibrary.Free`：在这里停掉后台线程/定时器，此后再调用宿主回调即属未定义行为。
- 日志统一走 `DshNativePluginRuntime.Log(level, message)`（激活前是静默的），不要写控制台。

**握手失败不再静默**
- 缺少导出符号（`dsh_plugin_package`/`dsh_plugin_entry`）、ABI 版本不匹配、库不是 `NativeLib=Shared` 产物时，装载失败会给出**结构化原因**：启动对 `plugins/` 下的候选逐文件记 WARN，`/plugins list` 增「加载失败」分组，`/plugins doctor` 打印完整原因。
- 因此"库放进 `plugins/` 却什么都没发生"时，直接 `/plugins doctor`；原因会按顺序标注是缺导出、版本不符、非共享库还是 quarantine。
- 对照：托管插件缺清单同样不再静默（见托管指南）。

**装载与更新**
- 主要分发方式是**放进 `<安装目录>/plugins/<目录>/` 启动加载**（原生库与托管插件同目录规则：目录名不参与识别）。
- `/plugins add <路径>` 现在**支持原生插件**：按扩展名（`.so`/`.dylib`，Windows 下的原生 `.dll`）与导出符号识别原生库，路由到原生装载；AOT 宿主不能热载托管 dll，但加载原生库可行。
- 更新与托管插件同规矩：**同名文件内容不一致不覆盖**。先 `/plugins remove <包名>`（或直接停宿主）→ 删掉旧目录 → 放入新库 → 重启。
- 卸载原生插件是 `Deactivate` + `NativeLibrary.Free`，没有可回收 ALC，因此没有托管那种"回收校验/leaked"标记。

**稳定性与崩溃隔离（进程内、按插件）**
- 每个原生插件对应一个 `NativePluginLoadContext`（`NativeLibrary` 句柄 + 导出绑定 + 在飞调用线程清单 + `Deactivate`/`Free` 路径），与托管侧 `PluginLoadContext` 结构对称。
- 宿主包装每次工具调用：捕获**可捕获的异常/ABI 错误**（如返回 null、JSON 解析失败）后，标记该插件 `Faulted` → 立即移除其全部工具并尽力卸载 → 静默写日志；同类故障**第二次**出现即加入 quarantine，此后 `TryLoad` 直接拒绝并可读原因。
- **诚实边界**：进程内遏制**不能**捕获原生段错误/访问违例（那需要 Windows VEH 或 POSIX `sigaction` + `sigsetjmp/siglongjmp`，本期**未落地**）；若真发生越界崩溃仍会带走整个宿主进程。当前策略是「调用包装 + Faulted 标记 + 卸载 + 二次即禁」，不是内存安全保证；插件仍须自己兜住异常与边界条件。

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
| 契约 | `IDshPlugin`（.NET 类型） | C ABI v1（日志 + 工具） |
| 能力面 | 服务/工具/提示词段/事件/入口点/配置 | 仅工具 + 日志 |
| 装载 | 启动扫描 + `/plugins add` 热装载托管 dll | 启动扫描 + `/plugins add` 热装载原生库 |
| 卸载 | 协作式卸载 + 可回收 ALC（有 leaked 校验） | `Deactivate` + `NativeLibrary.Free` |
| 版本闸门 | 清单 `DescriptorVersion` | ABI 版本（不符即结构化失败，`/plugins doctor` 可查） |
| 故障隔离 | ALC 隔离托管崩溃；原生依赖按 ALC 规则 | 进程内调用包装 + Faulted 卸载 + 二次 quarantine；原生段错误仍会带走进程（VEH/sigaction 未落地） |
| AOT 宿主 | 不能在运行期装载 | 正常加载 |
