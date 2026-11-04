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

   TUI 终端窗格会将光标位置、终端状态和设备属性查询的应答写回所属 PTY，供交互式 shell 完成终端初始化。

   注意：Linux 上 `vk` 后端要求 glibc >= 2.33(ppy.SDL3-CS 要求), 老系统请用 `gl` 或升级 glibc; GPU 后端不可用时 dsharp 会自动回退到 CPU 模式。

3. 使用 `/plugins` 命令族添加、卸载和管理插件；也可以将插件放进 `<安装目录>/plugins/`，启动时按当前执行角色、权限与沙箱设置发现和加载。托管插件模板见 `templates/managed-plugin`，原生示例见 `tests/Dsh.NativePluginSample`。
4. dsharp日志默认位置为`<home>/logs/dsh-YYYYMMDD.log`，可以在 `settings.yaml`→`logging:` 中配置它们(TUI 下强制关闭)。

### Settings mutation approvals

Model tools that change the active `HarnessHome.SettingsFile` share one settings mutation gate, including `write`, `edit`, `str_replace_editor`, and typed settings updates from `plugin_manager`.
The approval carries the canonical target, redacted changed-line diff, and before/after hashes; the audit records those fields plus the originating tool call, root call, decision, and commit outcome, not complete document snapshots.
Redaction recognizes common credential field names and known token formats; arbitrary custom secret names or formats may not be recognized.
The writer releases the settings lock while waiting for approval, then reacquires it and rejects a stale proposal rather than overwriting a concurrent human edit.
Approved content is published atomically through a private temporary file; existing readers that allow file deletion retain their previous snapshot.
Ordinary file tools also stage replacements before publication; write failures and cancellation preserve the previous target without recording a committed file change.
Normal explicit human settings commands and GUI saves remain human operations, not model tool calls.
Missing settings gate, session, or approval service fails closed for these model-originated updates.

```yaml
safety:
  autoApprove: false
  blacklist: ["bash:rm -rf /*"]
  whitelist: ["tool:write"]
```

`whitelist` automatically approves matching requests using exactly the existing blacklist tool/command grammar: `tool:<name>` and `<shell-tool>:<command-pattern>`.
Blacklist denial always wins, including when `autoApprove` is true; the session's `never` approval policy still rejects requests.
These patterns are not a path/arguments/intent permission engine, and shell command matching is not an OS isolation boundary.
Use the GUI safety page to edit both lists; TUI and GUI share the same authoritative approval result.

The trusted settings diff gate is always enabled for these tool/API paths, independently of `sandbox.enabled`. It does not intercept arbitrary BCL/native writes by host-role code, shell, PTC, editor, Git, remote MCP, or other unconfined processes. Enabling the plugin sandbox does not turn those paths into a universal settings-write boundary.

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

支持托管 JIT 插件与原生 NativeAOT 共享库插件，使用 `/plugins` 命令管理，也可以按 `plugins/<目录>/` 布局部署到安装目录。是否能加载、在哪个进程执行，以及可用的能力，由明确配置与实际平台后端决定；放入目录不等于授予宿主权限。

插件进入卸载后，新的名称或类型依赖不再基于该提供者激活；等待中的依赖插件会在替代服务可用后继续激活。

交互能力通过现有插件独立启用，不再选择四种互斥预设，也不再提供 `/preset` 命令。

| 插件 | 能力 | 使用边界 |
|---|---|---|
| `@deepseek-ai/dsh-ptc` | `run_code` 执行 C#，并通过 SDK 调用当前可见工具 | 可与原生工具调用混用，不改变 Workflow 生命周期 |
| `@deepseek-ai/dsh-csx` | `run_csx` 执行现有 `.csx` 文件，保留 `#load` 和脚本参数 | 需要已安装的 dotnet-script，不提供 PTC SDK |
| `@deepseek-ai/dsh-csharprepl` | `run_csharp_repl` 通过真实 CSharpRepl 计算一份完整 C# 输入 | 每次使用独立进程，保留输入内状态但不跨调用持久化 |
| `@deepseek-ai/dsh-tool-creative` | `creative_inspect_list`、`creative_inspect_query` 只读检查服务、配置和工具 | 事件检查仍不可用 |
| `@deepseek-ai/dsh-plugin-manager` | `plugin_manager` 管理插件 | 保留既有审批门禁，不由检查插件自动启用 |

通过 PTC 工具 SDK 发起的子调用仍遵循当前 scope、审批与审计；它与下面面向插件的 `IPluginHost` 能力 SDK 是不同接口。`run_code` 本身不是 C# 安全沙箱，也不受独立的 `scripts.permissions` 接管；不要用子工具的审批语义推断任意 PTC/BCL 代码也被隔离。
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
开启沙箱时，dotnet-script 的原生 `dotnet restore` 也受脚本网络权限约束；依赖尚未满足时，`network: false` 会在恢复阶段明确失败，而不是借用宿主私有缓存或凭据绕过限制。
需要在线恢复依赖时，应审查后显式允许脚本网络访问；前端安装、依赖恢复与脚本文件写权限是独立条件。

Creative 对用户插件和预置 `Dsh.*` 插件使用相同的开发指导：独立子进程、构建输出与中间目录，以及不共享现有实例状态的运行时 home。
模型默认通过 `ask_user_question` 询问进程处理方式，可选择隔离新实例、重启明确授权的自有实例，或为当前任务预授权自动处理；原有工具审批仍然生效。
只读检查工具本身不会构建、启动或终止进程，实际操作使用既有执行和自有终端工具。

不提供退役预设包的配置或历史兼容；升级前请自行从 `plugins` 配置中删除 `@deepseek-ai/dsh-presets`。
含旧预设事件且未标记为可忽略的会话，仍按通用未知事件规则拒绝读取。
会话起始模板（`.agent-presets`）和计划模式不受影响。

#### 插件执行角色与沙箱

`sandbox.enabled` 默认是 `false`，用于保留现有插件、shell、PTC、C# 前端与 MCP 的运行方式；此时配置的插件权限不是任意 BCL/原生调用的 OS 限制。可信工具与 typed settings API 对实际配置差异的审批始终启用，不随沙箱开关关闭。

需要 OS 保护时，在自己的 `settings.yaml` 中明确开启，并分别设置插件能力和脚本权限：

```yaml
sandbox:
  enabled: true
scripts:
  permissions:
    read: true
    write: false
    network: false
plugins:
  "@example/report":
    enabled: true
    execution: sandbox
    permissions:
      read: true
      write: false
      network: false
```

省略 `execution` 时，编译进当前应用镜像的插件默认在宿主执行，外部文件插件默认在共享沙箱执行；显式 `sandbox` 或 `host` 覆盖该缺省。
这一区分取决于实际制品形态，不取决于作者、包名前缀或预置包身份。
托管隔离插件必须实现 `IDshIsolatedPlugin`，原生插件使用同一 broker 操作。
完整 `Context` 或入口文件插件缺少 `execution: host` 时跳过并报告；编译插件显式选择不兼容的 `sandbox` 也使用相同失败汇总，扫描继续，取消仍传播。
权限值未指定时为允许，因此要按需要显式收紧 `read`、`write`、`network`。

隔离 worker 不直接取得业务目录、宿主凭据或网络访问。托管与原生插件通过同一组 broker 操作 `files.read`、`files.write`、`http.send` 请求能力；broker 检查对应插件的当前权限、路径与读写大小，写活动 settings 还需要真实工具上下文和 diff 审批。受限接口不暴露 `Context`、`IAgent` 或宿主服务对象。

外部完整 `IDshPlugin`/`Context` 组合插件需要显式选择完全信任的宿主角色：

```yaml
plugins:
  "@example/workspace-host":
    execution: host
    permissions:
      read: true
      write: false
      network: false
```

这些权限位只约束宿主角色通过 broker 发起的调用，不能撤销同地址空间代码的任意 BCL/原生访问。
GUI 提供默认、Sandbox、Host 三态执行选择，默认清除显式覆盖并恢复按制品形态的缺省；Host 表示完整进程内信任。
沙箱关闭时，执行选择只保存将来开启沙箱时使用的配置，当前运行方式仍是原有的进程内执行。
GUI 的三个权限开关与 TUI 的 `ban_read`/`enable_read`、`ban_write`/`enable_write`、`ban_network`/`enable_network` 都通过实际设置审批保存，不因插件来自编译镜像而禁用。
TUI 执行角色通过手工编辑 YAML 管理。
需要真正受限的执行，应迁移到能力接口，而不是仅修改角色名称或权限位。

模板不为预置插件写入批量宿主授权；已有配置无需补写角色即可采用上述中性缺省，`Dsh.Tool` 原有默认写能力/白名单不因此被收回。

**已经编译进主进程镜像的代码与模块初始化器都是宿主可信计算基（TCB）的一部分。** 事后把某个类型放入 worker，不能隔离它已经在主进程执行过的初始化器；不可信代码应作为独立文件部署的能力插件加载。

`/plugins add` 在实际共享 worker 中观察工件并检查将启用的执行角色，之后才进入 settings 差异审批与安装。
检查使用独占 staging 副本，完成探测和卸载后释放，拒绝、失败、取消也释放该次副本。
激活使用域拥有的独立副本，整个已安装插件目录及 `.shared` 保持布局；旧式根目录和外部单文件只复制同目录代码文件及 `.shared`。
运行时使用应用、dotnet host/shared、工具安装与 SDK 的只读目录根，业务 HOME 保持在代码根之外。
文件使用普通流与路径、权限和大小检查。

Worker 启动的单个15秒截止共同覆盖原生启动与 `sandbox/ready` 握手；启动完成后的工件观察另有20秒截止。
启动取消仍按取消处理，错误仅提供有界阶段（`launch-options`、`native-launch`、`protocol-setup`、`ready-request`）、启动任务状态、进程持有情况与清理前采样的取消状态，不包含路径或 worker stderr。
这些字段说明尚未完成的启动操作，不代表已经定位底层原因。
工件观察先于 settings 差异审批与安装，因此启动取消不是审批拒绝，也不生成拒绝审计。

权限变化在下一次 broker 操作即时生效，保留共享 worker 与 sibling 实例、状态。
独立释放一个插件的 lease 只卸载该包在 worker 中的激活、工具和 broker 绑定，其他包继续运行。
显式禁用、卸载、执行边界切换或故障恢复会退役旧共享 worker 并重建仍启用的成员，不重放失败或取消的调用。
拒绝配置审批不会提交对应变更。

`sandbox.enabled` 与宿主启动时的值不同会要求重启；热切换不会追溯隔离已经加载到主进程的代码。

#### 共享 worker 与平台边界

所有隔离插件共用一个 worker，托管与原生代码共享地址空间和故障后果。它们必须相互信任；broker 的逐插件权限不是抵御同进程恶意插件伪造调用身份或内存访问的边界。崩溃、超时或取消会影响共享 worker 中的成员，主进程保留并返回明确错误。

产品不提供 worker 内存上限、内存用量监测或内存耗尽保护；需要资源预算时，应由独立运行环境管理。

| 平台 | 必需条件与实际边界 |
|---|---|
| Linux | 需要可用的 bubblewrap 及所需 namespace、挂载能力，提供文件与网络隔离；后端或能力不可用时明确拒绝，不自动下载或改用普通进程。 |
| Windows | 使用 AppContainer 文件/网络边界及 Job Object 的进程树终止能力；可写目录使用显式授权。AppContainer 的 localhost/loopback 限制仍可能阻止本机服务连接，不自动添加 loopback exemption。 |
| macOS | 需要系统 `sandbox-exec`，保留单进程 Seatbelt 文件/网络隔离；要求派生子进程的执行明确拒绝，不声称已解决任意后代进程的所有权。 |

Windows 进程创建失败会报告实际 Win32 错误码；载荷不附着控制台，三条标准流仅继承明确列出的管道，AppContainer 与 Job 约束在恢复执行前建立。
Windows 子进程保留声明的工作目录，相对路径按该目录解析。
Windows 的单文件读取授权不包含父目录枚举；`cmd` 的文件名形式 `TYPE` 会先搜索目录，不能作为仅有单文件读取权限的通用消费者。
直接打开文件与列出父目录是不同的[文件访问权限](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights)，后端不会为命令的额外目录搜索扩大单文件授权。
可写目录内的只读或保护路径通过收窄当前 AppContainer SID 的 allow ACE 实现，不依赖包 SID 的 deny ACE；必要时暂时停止该对象的 DACL 继承，释放时按当前 ACL 移除本次 SID，并仅清除临时保护形成且内容未变的继承 ACE 副本，再恢复原继承状态；期间其他主体新增或修改的显式 ACE 不回滚。
Windows 引导仅为可执行文件与工作目录对象添加不继承的当前 AppContainer SID 读取/执行授权；打开这两个对象及撤销引导授权只请求 `READ_CONTROL | WRITE_DAC`，不请求数据写入、删除或最大可用权限。正常 ACL 写入仍传播其他主体已有的可继承 ACE，不禁用系统继承传播。
同一 SID 已继承、有效且完全匹配所需传播范围的授权会直接复用，不重复改写运行时或可写目录后代的 DACL；额外的环境组授权检查及保护路径的过量权限收窄仍然执行。
只读授权还会检查已创建但尚未恢复执行的实际子进程 token：复用已有只读权限前，逐一读取每个现有对象的 ACL、所有者、组与完整性标签，只有稳定的纯 allow ACL 已允许该 token 读取/执行，且目录的稳定继承 ACE 同时能证明未来文件与目录的权限时，才免去添加本次 SID。泛型权限按实际文件继承规则映射；无法取得 token、查询失败、权限或未来继承无法证明时，回到原有逐对象授权与收窄路径，不省略后代检查。完整策略在受 Job 管理的暂停子进程恢复执行前完成。
只读证明的宿主检查句柄仅请求 `READ_CONTROL`；只有确实需要修改 DACL 时，才通过 [`ReOpenFile`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-reopenfile) 为同一文件系统对象取得原策略编辑权限，完整子树检查、实际子进程 token 校验与权限撤销保持不变。
同权限目录根先处理祖先，并复用已经枚举的子树；不同权限的子树仍完整处理，应用、工具安装与 SDK 的实际只读目录根保持不变。
同进程并发沙箱的 ACL 读改写与原生继承传播串行完成，各自只移除自身 SID；临时 DACL 保护与可写完整性标签由仍存活的路径租约共同持有，最后一个使用者释放时才恢复，避免先结束的沙箱撤销另一个沙箱仍需要的边界或写入能力。
Windows 启动在目录枚举、授权和进程创建/恢复前观察原调用取消；取消后的已获授权仍按原清理路径撤销，不继续启动迟到的载荷，也不重放请求。
Windows 的 `SetSecurityInfo` 可能把旧式 DACL 转换到[自动继承模型](https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces)并设置 SDDL 的 `AI` 标记；恢复契约保留全部 ACE 的顺序、权限与标志及原继承保护状态，并保持后续实际继承与保护行为，不要求撤销该系统元数据转换。
若既有 AppContainer 组或本次启用的 capability SID 在保护对象或相关祖先上已有额外写入、删除或改权限的 allow，后端明确拒绝启动，不修改其他 SID 来伪装通用 deny 支持。
保护路径的可写祖先目录不授予自身删除权限，但新建的普通工作区文件仍可继承正常修改、改名与删除权限。
AppContainer 的应用数据目录由 Windows 按当前容器身份重定向，用作容器自有临时区；这不授予宿主工作目录写权限，也不意味着继承完整宿主环境。

macOS 的 dyld 共享缓存引导需要读取根目录对象；该权限仅针对 `/` 目录本身，不是递归读取根目录下文件内容的授权。
托管 worker 的全球化运行时可只读系统 `/usr/share/icu` 数据；该例外不授予写入、执行或读取其他宿主数据的权限。
单进程镜像替换只允许执行明确授权的只读输入；目录读取授权不自动变成执行授权。

`scripts.permissions.read/write/network` 只用于实际脚本执行器 `run_csx` 和 `run_csharp_repl`，不从执行器插件权限推导，未指定的值为允许。
沙箱开启时，脚本工作目录的数据访问按这些权限授权，运行时代码只读输入和独占临时目录另外授权；禁止工作目录写入仍可在私有临时目录计算。
Linux 的 `read: false, write: false` 使用空的 guest 工作目录；`read: false, write: true` 无法兑现只写目录语义，明确拒绝。
后续脚本调用读取当前权限；关闭沙箱时保留原有执行方式。

可归属的本机 MCP stdio 进程保留显式 OS 沙箱启动路径，不支持的后端/子进程需求明确失败。shell、持久 shell、PTC、原生 PTY、编辑器、Git、远程和不可归属 MCP 不被这套脚本策略全局接管，也不因开启插件沙箱而自动获得 OS 保护。

#### 能力 SDK 与原生 ABI

`IPluginHost`、`IDshIsolatedPlugin` 与 `PluginHostOperations` 位于 `Dsh.Plugins.Abstractions`，命名空间仍为 `Dsh.Plugins`。托管隔离插件通过 `InitializeAsync(IPluginHost, JsonElement, CancellationToken)` 初始化，使用 `RegisterTool`、`CallAsync` 和 `Log`；插件引用宿主提供的同一契约，不打包第二份宿主契约程序集。

`templates/managed-plugin` 保留 `IDshPlugin.Apply(Context, ...)` 生命周期、配置和 prompt 注入，并演示能力调用；它不是隔离插件模板，开启沙箱时仍需显式宿主角色。原生示例见 `tests/Dsh.NativePluginSample`。

- `files.read`：参数 `{path, offset?, count?}`，返回 `{data, length, eof}`，`data` 为 base64。
- `files.write`：参数 `{path, data, append?}`，`data` 为 base64，返回 `{bytesWritten}`。
- `http.send`：参数 `{url, method?, headers?, body?}`，`body` 为 base64，返回 `{status, headers, body}`。

原生 ABI 保留 **v1** 版本、原入口和旧表前缀，在宿主 API 尾部追加可选 `HostCall`。新增可选入口 `dsh_plugin_entry_with_host_size` 用实际表长协商新能力；仅有版本号不能证明尾部字段存在，新插件在旧宿主中只读取旧前缀。原有 v1 二进制不要求重建。

`NativePluginContext.CallHost` / `DshNativePluginRuntime.CallHost` 使用上述操作与 JSON 语义，当前宿主以一次同步调用和完整响应缓冲执行，不通过重复调用业务操作探测结果大小。原生 SDK 同样提供 `Native.Host.SettingsJson.Write`。

原生插件的逻辑停用会注销工具并释放本次激活资源，但镜像可驻留到所属进程退出。[NativeAOT 不支持通过 `dlclose` / `FreeLibrary` 卸载共享库](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)；替换原生工件后需要新 worker 或重启宿主，不宣称普通加载器会识别内容变化或逻辑卸载能回收镜像。

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
<summary><b>未发布</b> — 插件隔离与配置变更审批</summary>

- 增加默认关闭的插件沙箱；预置与用户插件使用同一显式执行角色、read/write/network 配置和权限修改入口。
- 托管/原生能力插件共用一个 worker 和 broker，保留最小平台隔离及崩溃清理，不提供插件间内存隔离或内存预算。
- 模型经内置写入工具、插件管理和 broker 修改活动 settings 时，共用一次 diff 审批与精简审计；人工保存和不受管的宿主执行路径保持明确边界。
- 原生 ABI 保留 v1 兼容与可选能力表长协商，脚本权限只作用于真实 C# 脚本执行器。
- 修复移除独立插件协议包后，CLI/GUI/Web 裁剪发布仍引用旧程序集的问题。

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
- SSH 借用出口区分跳板 `-W` 目标连接失败与本机转发监听失败，避免将 `stdio forwarding failed` 误报为本机端口绑定冲突。
- 修复 SSH 退出与连接关闭并发时的回收死锁；故障请求仍不自动重放，只由新的请求重建出口。

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
