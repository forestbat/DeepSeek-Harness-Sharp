# DeepSeek-Harness-Sharp

一个用 C# 重写的 DeepSeek Harness 实现,初衷是解决原 DeepSeek Harness 在高并发下性能减退和崩溃的问题(TUI 单进程内存占用约 10MB，GUI 单进程内存占用约50-100MB)。

## 安装

从 GitHub Release 下载对应平台的 Release 构建，装到用户目录并注册 `dsharp` 命令:

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.ps1 | iex
```

安装后新开终端,在任意目录运行 `dsharp` 即可启动 TUI。

可用环境变量: `DSHARP_VERSION`(版本号或 latest)、`DSHARP_HOME`(安装目录)、`DSHARP_BASE_URL`(镜像基址)、`DSHARP_PROXY`(下载代理)。

## 运行

1. 配置文件:`~/.dsh/settings.yaml`(Linux)/`%USERPROFILE%\.dsh\settings.yaml`(Windows);

   首次运行使用带注释的模板作为默认配置,保存设置时才写入文件,不会覆盖已有配置;插件开关与参数写在 `plugins:` 段。
2. 测试性构建与运行:
   ```bash
   dotnet build # 只构建不运行
   dotnet run --project DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj  # 执行构建并启动
   DeepSeek-Harness-Sharp/bin/Debug(Release)/net10.0/dsharp 启动构建结果
   ```
   `DSH_HOME`(或 `settings.yaml` 的 `storage.root`)可以指定独立的配置/会话目录,便于用不同配置试跑构建产物。

   ```bash
   # 默认入口: 交互式 TUI
   dsharp --gpu # 使用GPU加速渲染，会启动独立窗口
   # 无头单次任务
   dsharp -- headless "跑一遍测试"
   # 从磁盘恢复已退出的对话
   dsharp (tui) --session <session-id>  # 启动/恢复特定会话，加入--gpu参数则在独立 GPU 窗口恢复
   dsharp (tui) attach <session-id> # 同上
   # 后台 PTY 会话管理(守护进程按需自动拉起, 无需手动启动)
   dsharp (tui) list  # 列出 daemon 管理的 PTY 会话
   dsharp (tui) attach <pty-id>  # 接回仍在运行的后台会话(`pty-id` 标识 daemon 管理的进程)，加入--gpu参数则在独立 GPU 窗口恢复
   ```

   注意：Linux 上 `vk` 后端要求 glibc >= 2.33(ppy.SDL3-CS 要求), 老系统请用 `gl` 或升级 glibc; GPU 后端不可用时 dsharp 会自动回退到 CPU 模式。

3. 使用 `/plugins` 命令族添加、卸载和管理插件；也可以将插件放进 `<安装目录>/plugins/`，就可以自动加载并启用该插件。插件编写示例可见 `tests/Dsh.NativePluginSample`。
4. dsharp日志默认位置为`<home>/logs/dsh-YYYYMMDD.log`，可以在 `settings.yaml`→`logging:` 中配置它们(TUI 下强制关闭)。

## 不同于原版 DSH 的功能

### TUI

Dsh.Tui 工作逻辑类似于 tmux，会启动守护进程和伪终端，为 AI 任务保活，以防用户退出终端或网络抖动时，任务自动终止；此外，TUI还支持 Vulkan/OpenGL GPU 渲染加速，如果你使用 llama.cpp 部署模型，可以使用同一个 Vulkan 后端，实现原生命令行。

<details>
<summary>TUI 配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-tui":
    gpuBackend: auto          # auto|vk|gl|cpu
    vsync: true
    keys:
      overview: F5
      newSession: "Ctrl+X Ctrl+N"
      splitPane: "Ctrl+X |"
    keySequenceTimeoutMs: 1500
    highlightColor: "#7fd1ff" # 支持真彩色颜色配置
    dividerColor: BrightBlack
```
</details>

<details>
<summary>0.4.0 待发布：ChoicePrompt 问答与审批</summary>

- 问答与审批接管键盘和鼠标，不向聊天、全局快捷键或 shell 穿透；关闭后恢复聊天草稿、光标和焦点。
- 方向键与 `Home` / `End` 导航，`Space` 或 `1-9` 选择，`Tab` / `Shift+Tab` 切换选项、自定义输入、提交和取消，`Enter` 确认；滚轮或 `PageUp` / `PageDown` 浏览长内容，支持中文折行后的点击与悬停。
- 问答 `Esc` 取消整个题组并丢弃部分答案，返回 `{"status":"dismissed","answers":[],"reason":"user_cancelled"}`，区别于没有问答提供者或服务错误。
- 审批提供 `Allow once` (`y`)、`Always` (`a`，仅当前会话的该工具，fork 不继承)、`Reject` (`n`) 和 `Cancel` (`c` / `Esc`)；取消不放行工具或取消整个回合，带 `Ctrl` / `Alt` 的快捷键不授权。
- GUI/Web 使用相同的选项和结果契约；Host 待决投影与 request id 为唯一权威，先答者生效。GUI 主聊天只自动打开当前机器、当前会话的提示，其他提示可从通知中心回答；调用方撤销等待会移除自己的请求。
- 仅在 Host 宣告 `decision-cancellation-v1` 时启用问答整组取消与审批 Cancel；旧 v2 Host 显示 Unsupported、保留未决项，正常回答与拒绝仍可用。ACP 按实际提供的选项启用按钮，不代用更高权限，未被接受的响应保留待决状态。

</details>

### GUI

本项目的 GUI 使用 Avalonia UI 原生实现，与 TUI 共用配置、会话和命令，双击安装包里的`dsh-gui.exe`即可打开，也可以从命令行启动。

```bash
Dsh.GuiHost/bin/Debug/net10.0/dsh-gui.exe   # Windows
dsharp gui                                  # 从控制台宿主进入 GUI
```

<details>
<summary>GUI 配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-gui":
    theme: dark               # dark|light|system
    fontSize: 13.5
    closeAction: tray         # tray|quit|ask
    workspaceView: solution   # solution|filesystem
    sidebarVisible: true
    gpu: { enabled: true, adapter: auto, backend: auto }
    window: { rememberBounds: true }
```
</details>

### 编辑器与 IDE支持

`@deepseek-ai/dsh-ide` 提供外部编辑器启动、文件/行列定位、diff、历史记录和客户端桥接等功能。

```text
/ide list
/ide doctor
/ide open README.md --line 1 --column 1 --pick
/ide open --changed
/ide diff --git --pick
/ide diff --checkpoint --pick
/ide status
/ide why
```

用户可以在 GUI 的「设置 → IDE 与编辑器」调整编辑器顺序（自动打开机制会选取最上面的一个启动）与触发规则（例如，用户可以配置在打开新会话时，启动想要的IDE）；「打开方式」可选择编辑器及本地/远端目标。

<details>
<summary>IDE 配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-ide":
    editors:
      - id: rider
        remote_executables: [/opt/rider/bin/rider]
      - id: vscode
      - id: nvim
    default_editor: auto
    focus: never
    reuse_window: auto
    history:
      timeline: true
      timeline_limit: 50
      window_seconds: 60
    client_mappings:
      - machine: build
        source_root: /home/dev/project
        destination_root: C:\work\project
        kind: local_mirror
```
</details>

### 多机与远程会话

dsharp 有完善的多机远程支持(Dsh.Fleet)，同时可以在 GUI 中直接配置远程机器、打开远程工作区。

示意图：

<img width="1470" height="285" alt="图片" src="https://github.com/user-attachments/assets/f0208d99-8c2d-490a-a97b-6606bd8f2dc3" />

<img width="1703" height="833" alt="图片" src="https://github.com/user-attachments/assets/7548b1a8-8732-4241-8384-f1b62fa70570" />

运维命令：
```bash
dsharp machine list | add | remove | test | deploy
dsharp session list [--machine build]
dsharp session attach build/<id>
dsharp session stop build/<id>
dsharp doctor [--machine build]
dsharp host serve
dsharp machine proxy build show | set | test | clear
dsharp machine tunnel build set | show | test | use | direct | clear
dsharp doctor --proxy-layer ssh | local_outbound | remote_outbound
```

<details>
<summary>Fleet 配置</summary>

```yaml
fleet:
  machines:
    - name: build
      kind: ssh
      host: build.internal
      user: dev
      auth: key            # agent | key | password
      keyPath: ~/.ssh/id_ed25519
  deploy:
    autoDeploy: true

network:
  outbound_proxy:
    url: http://127.0.0.1:7890
    bypass: [example.com, "*.example.org"]
    probe_url: https://www.gstatic.com/generate_204
```
</details>

### 多 Agent 协作

目前采取 Agent（主会话）-SubAgent 模式进行多 Agent 协作，支持多个 Agent 构成的森林；SubAgent 之间通过共享看板协调，也可接入 A2A 与外部 Agent。

- SubAgent 支持 `spawn` 独立上下文/`fork` 继承当前上下文两种派生方法，支持后台运行、模型选择、工具过滤与委派深度等功能或配置。
- 工具：`subagent`、`list_subagent_models`、`send_message`、`interrupt_agent`、`list_agents`。
- 共享看板：`board_post`、`board_read`、`board_claim`、`board_release`。
- 看板软行锁：按路径和行区间声明编辑意图，以防其他 SubAgent 乱改，`ttl` 到期自动失效；重叠声明返回冲突，只作 advisory。
- 大规模编排：使用 `workflow`(Dsh.Workflow) 管理持久工作流，或用 `run_code`(Dsh.Ptc) 通过 C# 批量启动子 Agent；代码调用与原生工具可以在同一会话中组合，无需切换模式。
- A2A支持：带有`a2a_card`、`a2a_send`、`a2a_get`、`a2a_list`、`a2a_cancel`工具，可作 A2A server 或连接远端 Agent。
- 外部 Agent：目前支持 Codex、Claude Code、OpenCode、Kilo Code 或任意 ACP Agent。

<details>
<summary>多 Agent 配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-subagent":
    provider: spawn
    toolName: subagent
    enableRunInBackground: true
    backgroundMode: one-shot
    maxDepth: 3
    agentOptions:
      provider: deepseek-official
      model: deepseek-v4-flash
      maxTokens: 8192
    toolFilter:
      allow: [read, grep]
  "@deepseek-ai/dsh-workflow-worker-thread":
    subagentProvider: spawn
    maxRounds: 256

a2a:
  enabled: true
  host: 127.0.0.1
  port: 0
  publicUrl: https://agent.example.com
  authToken: <token>
  skill_id: coding
  skill_name: Coding
  remotes:
    peer:
      url: https://peer.example.com
      token: <token>
```
</details>

### 会话、回滚与持久化

可以通过启用`Dsh.Checkpoints`插件，自动生成项目检查点，以便使用`/timestamp`命令回滚。

<details>
<summary>相关配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-checkpoints":
    maxPoints: 256
    keepDays: 15

storage:
  root: ~/.dsh
  tool_results:
    max_bytes: 65536
    retain_days: 30
```
</details>

### 插件与扩展

支持 C# JIT 编译和 AOT 编译两种插件，使用 `/plugins` 命令管理，将符合规范的插件放入 `<安装目录>/plugins/` 也可以自动加载。

交互能力通过现有插件独立启用，不再选择四种互斥预设，也不再提供 `/preset` 命令。

| 插件 | 能力 | 使用边界 |
|---|---|---|
| `@deepseek-ai/dsh-ptc` | `run_code` 执行 C#，并通过 SDK 调用当前可见工具 | 可与原生工具调用混用，不改变 Workflow 生命周期 |
| `@deepseek-ai/dsh-csx` | `run_csx` 执行现有 `.csx` 文件，保留 `#load` 和脚本参数 | 需要已安装的 dotnet-script，不提供 PTC SDK |
| `@deepseek-ai/dsh-csharprepl` | `run_csharp_repl` 通过真实 CSharpRepl 计算一份完整 C# 输入 | 每次使用独立进程，保留输入内状态但不跨调用持久化 |
| `@deepseek-ai/dsh-tool-creative` | `creative_inspect_list`、`creative_inspect_query` 只读检查服务、配置和工具 | 事件检查仍不可用 |
| `@deepseek-ai/dsh-plugin-manager` | `plugin_manager` 管理插件 | 保留既有审批门禁，不由检查插件自动启用 |

通过 C# SDK 发起的工具子调用仍遵循当前 scope、审批与审计；`run_code` 本身不是 C# 安全沙箱，也不限制用户项目采用的外部工具链。
SDK 返回的 JSON 根节点可以直接组合进新的 `JsonObject` 或 `JsonArray`；工具返回的图片会作为模型上下文单独交付。

PTC 的临时调试开关默认关闭，启用后会提示模型优先将独立、非交互的工具操作归并到一次 C# `run_code`，而不是分别发起原生调用（包括并行调用）。
这只是生成偏好，不强制工具路由。
关闭时仍可正常选择原生工具或 PTC；批量调用不跳过依赖、审批或用户决策。

```yaml
plugins:
  "@deepseek-ai/dsh-ptc":
    debug_batching: false
```

独立 C# 前端按需安装，dsharp 不自动下载或捆绑它们；缺少前端时，对应工具返回可操作的错误，其他能力仍可使用。
以下命令将已验证的 [dotnet-script 2.0.1](https://github.com/dotnet-script/dotnet-script/tree/2.0.1) 和 [CSharpRepl 0.9.2](https://github.com/waf/CSharpRepl/releases/tag/v0.9.2) 安装到当前工作区：

```sh
dotnet tool install dotnet-script --version 2.0.1 --tool-path .artifacts/csharp-frontends --allow-roll-forward
dotnet tool install csharprepl --version 0.9.2 --tool-path .artifacts/csharp-frontends --allow-roll-forward
```

通过 `settings.yaml` 指定可执行文件；相对路径基于宿主启动目录，Windows 请使用安装后对应的 `.exe` 文件名。

```yaml
plugins:
  "@deepseek-ai/dsh-csx":
    executable: .artifacts/csharp-frontends/dotnet-script
  "@deepseek-ai/dsh-csharprepl":
    executable: .artifacts/csharp-frontends/csharprepl
```

`run_csx` 接收 `script` 文件路径与可选的 `arguments` 数组，`run_csharp_repl` 接收 `code` 和 `description`；两者支持 `workdir`、`timeoutMs`，均不提供 `run_code` 的工具 SDK。
Windows 的 `run_csx` 在可写的 `workdir` 内创建临时 `.dsh-csx-*` 目录，通过真实 `#load` 执行未经改写的原文件，并在原脚本执行前初始化 UTF-8 脚本输出；引导目录随执行结束清理，编译缓存由原生前端管理。
CSharpRepl 工具保留 OS 退出码和完整托管退出码，避免 Unix 截断错误码后将异常当作成功；普通 stderr 输出不会被误判为失败。

Creative 对用户插件和预置 `Dsh.*` 插件使用相同的开发指导：独立子进程、构建输出与中间目录，以及不共享现有实例状态的运行时 home。
模型默认通过 `ask_user_question` 询问进程处理方式，可选择隔离新实例、重启明确授权的自有实例，或为当前任务预授权自动处理；原有工具审批仍然生效。
只读检查工具本身不会构建、启动或终止进程，实际操作使用既有执行和自有终端工具。

不提供退役预设包的配置或历史兼容；升级前请自行从 `plugins` 配置中删除 `@deepseek-ai/dsh-presets`。
含旧预设事件且未标记为可忽略的会话，仍按通用未知事件规则拒绝读取。
会话起始模板（`.agent-presets`）和计划模式不受影响。

<details>
<summary>插件配置</summary>

```yaml
plugins:
  "@deepseek-ai/dsh-memory":
    backend: file
    file: ~/.dsh/memory/project.md
  "@deepseek-ai/dsh-checkpoints": false
  "@deepseek-ai/dsh-toon": false

sandbox:
  enabled: false
```
</details>

### 其他功能

支持 DeepSeek 账号登陆；
支持 TOON 格式输出，节省 token 消耗；
支持搜索会话内容。

## 各版本新增功能

<details>
<summary><b>0.4.0 待发布</b> — ChoicePrompt 问答与审批</summary>

- TUI 问答与既有审批共用键鼠选择控件，支持多选、自定义输入、禁用/默认、中文折行命中、长内容滚动和草稿光标恢复。
- 问答整组取消返回结构化 `dismissed`，审批保留当前会话工具授权边界，GUI/Web 复用选择与结果契约及 Host 先答者生效生命周期。
- 决策取消采用显式 Host 能力门禁：旧 v2 Host 不会把取消误读为空答案或拒绝；GUI/Web 显示 Unsupported、保留未决项，既有回答与拒绝继续可用。

</details>

<details>
<summary><b>未发布</b> — 交互模式能力化</summary>

- 移除 `Dsh.Presets`、四种互斥交互模式及 GUI/TUI 预设入口，`run_code` 与原生工具可直接组合。
- 运行时检查和插件管理独立注册工具与提示，不再依赖创造模式，也不互相自动启用。
- 直接移除退役预设包，不提供专用配置迁移或旧事件兼容；现有 scope、审批、审计及 Workflow 生命周期保持不变。
- 检查能力统一使用 Creative 命名，并提供隔离构建与进程指导：默认询问用户，也支持明确授权的任务级自动操作。
- 增加默认关闭的 PTC `debug_batching`，仅调整独立工具调用的批量生成偏好，不改变执行门禁。
- 增加真实 dotnet-script 与 CSharpRepl 前端工具，独立执行 C# 脚本或输入，保留诊断、取消和自有进程清理。
- 修复 SDK 返回值组合时的 JSON 节点所有权、无反射宿主中的 csx 失败诊断、Windows csx 中文输出，以及 CSharpRepl 相对可执行路径解析。

</details>

<details>
<summary><b>未发布</b> — GUI 后端解耦</summary>

- GUI 改由中性工作区契约连接后端，会话、问答、文件/Git、编辑器、终端和机器设置保留现有入口；释放工作区不停止 Host 持有的会话。
- 工作区作为独立插件管理后端连接，不依赖 IDE 插件；未启用 IDE 时仍可使用会话、终端和文件能力。
- 新增 Kilo Code 内置适配器，支持 ACP 会话与交互终端入口。
- 内置 Host 新建或恢复会话时继承启动默认模型与 reasoning；持久化的会话选择仍优先，完全未配置模型时仍明确报错。

</details>

<details>
<summary><b>0.3.1</b> — Dsh.Ide 编辑器集成</summary>

- 编辑器检测、有序定义、宿主识别、安装目录
- `/ide` 命令族
- GUI 编辑器设置与打开方式
- VS Code/JetBrains/Zed 历史与时间线
- 编辑器触发器
- 远端客户端桥接与路径映射
- LSP 与扩展桥接
</details>

<details>
<summary><b>0.3.0</b> — 远程宿主拆分、Iroh 传输与 GUI 工作区</summary>

- 远程 Host 客户端/服务端契约；SSH/Iroh 可选传输
- Iroh 机器配置与接收端命令
- SSH 代理、借用出口、分层代理探测
- GUI 远端会话、Git 页面、机器编辑、目录选择
- 图片读取工具；文本引用不再自动解析图片
- 配置模板统一
</details>

<details>
<summary><b>0.2.0</b> — Vulkan、主题与设置面</summary>

- Vulkan 渲染后端与 GPU 宿主抽象
- 时间线功能
- 模型列表、存储根统一
</details>

<details>
<summary><b>0.1.2</b> — 远程工作区打磨</summary>

- 远端会话跟随客户端模型
- 远端部署按发布版本取键，过期守护进程重启
- 修复 Avalonia 托盘图标；升级 Avalonia
- GUI 远端会话列表滚动区
</details>

<details>
<summary><b>0.1.1</b> — SSH 远程工作区、macOS、TUI</summary>

- SSH 远程工作区、中立传输、远端宿主、自动部署
- `dsh` 改名 `dsharp`
- macOS CI/JIT 发布
- TUI 真彩色颜色配置、更多`Ctrl+X`功能
- 时间线/回滚、检查点
- 看板软行锁
</details>

<details>
<summary><b>0.1.0</b> — 第一版更新 </summary>

https://github.com/deepseek-ai/deepseek-harness/discussions/8952
</details>
