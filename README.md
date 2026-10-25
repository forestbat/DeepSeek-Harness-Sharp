# DeepSeek-Harness-Sharp

一个用 C# 重写的 DeepSeek Harness 实现,初衷是解决原 DeepSeek Harness 在高并发下性能减退和崩溃的问题(TUI 单进程内存占用约 10MB，GUI 单进程内存占用约50-100MB)。

## 安装

从 GitHub Release 下载对应平台的 Release 构建,装到用户目录并注册 `dsharp` 命令:

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.ps1 | iex
```

安装后新开终端,在任意目录运行 `dsharp` 即可启动 TUI。可用环境变量覆盖: `DSHARP_VERSION`(版本号或 latest)、`DSHARP_HOME`(安装目录)、`DSHARP_BASE_URL`(镜像基址)、`DSHARP_PROXY`(下载代理)。Release 资产命名为 `dsharp-<rid>.tar.gz`(Linux/macOS)与 `dsharp-win-<arch>.zip`(Windows)。

SQLite 是可选的系统依赖,发布包不捆绑或自动下载 SQLite 原生库。Linux 使用系统 `libsqlite3.so.0`(例如发行版的 `libsqlite3-0`/`sqlite-libs` 运行库),macOS 使用系统 `/usr/lib/libsqlite3.dylib`,Windows 使用系统 `winsqlite3.dll`。只有该系统库支持 FTS5 时才启用会话全文索引;不同系统版本的能力可能不同,尤其不能假定 Windows 自带 SQLite 支持 FTS5。没有系统 SQLite、缺少所需入口或未编入 FTS5 时,会话搜索自动使用进程内、不区分大小写的文本匹配(多词 AND),`/session-query` 和 `session_search` 仍可查询历史与活会话,不会阻断正常会话、ACP 或 TUI 启动。全文分词匹配与本地子串匹配的结果可能不同;会话文件仍是数据来源,切换后端不迁移或丢弃会话。

## 运行

1. 配置文件:`~/.dsh/settings.yaml`(Linux)/`%USERPROFILE%\.dsh\settings.yaml`(Windows);首次运行使用带注释的模板作为默认配置,保存设置时才写入文件,不会覆盖已有配置;插件开关与参数写在 `plugins:` 段。
2. 构建与运行:
   ```bash
   dotnet build # 只构建不运行
   dotnet run --project DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj  # 执行构建并启动
   DeepSeek-Harness-Sharp/bin/Debug(Release)/net10.0/dsharp 启动构建结果
   ```
   主程序叫做dsharp/dsharp.exe；`DSH_HOME`(或 settings.yaml 的 `storage.root`)可以指定独立的配置/会话目录,便于用不同配置试跑构建产物。

   ```bash
   # 默认入口: 交互式 TUI
   dsharp --gpu # 使用GPU加速渲染，会启动独立窗口
   # 无头单次任务
   dsharp -- headless "跑一遍测试"
   # 从磁盘恢复已退出的对话
   dsharp tui --session <session-id>
   dsharp --session <session-id>  # 默认进入 TUI
   dsharp tui --gpu --session <session-id>  # 在独立 GPU 窗口恢复
   # 后台 PTY 会话管理(守护进程按需自动拉起, 无需手动启动)
   dsharp tui list  # 列出 daemon 管理的 PTY 会话
   dsharp tui attach <pty-id>  # 接回仍在运行的后台会话
   dsharp tui attach <pty-id> --gpu  # 用独立 GPU 窗口接回
   ```
   `session-id` 标识磁盘里的对话, `pty-id` 标识 daemon 管理的进程; `tui attach <session-id>` 也可以从磁盘恢复对话。
   `--session` 可以放在 `tui` 之前, 如 `dsharp --session <session-id> tui`。
   恢复会显示原有历史并继续使用原会话; ID 不存在或日志损坏时报告错误并以非零状态退出, 不新建替代会话。

   TUI 的 GPU 渲染后端由 `plugins."@deepseek-ai/dsh-tui"` 的 `gpuBackend` 配置项决定(`auto|vk|gl|cpu`,缺省 `auto`，优先以 `vk` 后端启动)，`vsync` 即垂直同步默认为 `true`。

   Linux 上 `vk` 后端要求 glibc >= 2.33(ppy.SDL3-CS 要求), 老系统请用 `gl` 或升级 glibc; 后端不可用时 dsharp 会输出原因并自动回退(`gl` -> CPU)。

   终端 TUI 的后台 Git 面板、检查点和命令工具不继承交互终端输入,刷新或执行命令期间仍可逐键输入。普通命令的标准输入立即收到 EOF;需要持续输入的管道协议(如 `bash_persistent`)单独保持输入管道,交互式 shell 仍使用独立 PTY。窗口调整和子进程退出信号不会被误当作输入结束。正常 `/exit` 会恢复终端行模式、回显和主屏幕。

3. 插件运维:TUI 内用 `/plugins list|add <包|dll 路径>|remove <包> [--force]|disable <包>|enable <包>|ban_write <包>|enable_write <包>`(输入 `/plugins` 后按 Tab 可从浮层选单选子命令与包名);改动写回 `settings.yaml` 的 `plugins:` 段并保留注释。

    `ban_write`/`enable_write` 切换该插件的写权限(`plugins."<包>".permissions.write`),`enable`/`disable` 保留权限设置,`remove` 连同参数与权限一并清除。权限目前只记录在配置里,由后续版本的插件沙箱强制执行。
    `disable` 只停用效果、保留插件(`enable` 可复原);`remove` 是卸载:摘除效果并删除 `plugins/<包名>/` 目录(内置或手工平铺的插件没有该目录,只做摘除)。更新插件:直接 `remove` 旧版(安装目录会被一并删除),再 `add` 新版;`add` 遇到同名但内容不同的文件会拒绝,以免覆盖手工放置的同名插件。
    托管插件的配置面随其可回收加载上下文卸载而移除,不会留在后续的配置模板或设置列表中。
4. 日志:`<home>/logs/dsh-YYYYMMDD.log`(默认开启);`logging:` 段可配置级别、内存缓冲、是否输出到控制台(TUI 下强制关闭)。
5. 后台会话:TUI 内不带命令的 `/detach`(Ctrl+X D)保留当前对话和未发送草稿, 交由 PTY 守护进程继续运行。
   `/detach <命令>` 托管指定命令;通过 `tui list` 查找 PTY ID, 再用 `tui attach <pty-id>` 接回。
6. 插件目录:把插件放进 `<安装目录>/plugins/`,启动时自动加载并启用(可用 `/plugins disable <包>` 关闭)。托管插件是构建时引入 `Dsh.Plugins.Generator` 的 dll(生成清单提供入口),仅在默认 JIT 发布档可用;原生插件是 `.so`/`.dylib`/`.dll` 共享库(实现 `IDshNativePlugin` 声明包名与工具,导出握手与 ABI 胶水由生成器产出),JIT 与 AOT 发布档均可用,示例见 `tests/Dsh.NativePluginSample`。
7. 看图:模型可调用 `read_image` 工具读取本地图片(PNG/JPEG/GIF/WebP/BMP/TIFF 等常见格式,无扩展名的文件按内容识别),图片本身随工具结果交给模型;路径解析与 `read` 相同(相对路径按会话工作目录)。
   - 长边超过 2000px 的图片自动降采样,结果里写明原始尺寸与坐标换算倍数;源文件上限 20 MiB,存入附件库的图片不超过 5 MiB(超出时改用 JPEG 压缩)。
   - 需要接收图片输入的模型:DeepSeek 以内置模型目录为准(如 `deepseek-v4-flash-vision-exp`),Anthropic 与 OpenAI 兼容提供方以 models.dev 标注的输入模态为准;当前模型明确不支持图片时 `read_image` 直接报错,未收录的模型交由提供方自行判定。
8. TUI 快捷键:默认是 `Ctrl+X` 前缀序列(如 `Ctrl+X W` 总览、`Ctrl+X N` 新会话、`Ctrl+X +`/`Ctrl+X -` 分屏/关窗格;按下 `Ctrl+X` 后状态行会列出可用的后续键),TUI 内 `/keys` 列出全部动作与当前绑定。改键写在 `settings.yaml` 的 `plugins."@deepseek-ai/dsh-tui".keys` 下,格式为 `动作名: 按键串`,修饰键(`Ctrl`/`Alt`)用 `+` 连接,多键序列用空格分隔,未写的动作保持默认:

   ```yaml
   plugins:
     "@deepseek-ai/dsh-tui":
       keys:
         overview: F5                  # 单键打开总览
         newSession: "Ctrl+X Ctrl+N"
         splitPane: "Ctrl+X |"
       keySequenceTimeoutMs: 1500      # 前缀等待超时(毫秒),缺省 0 = 一直等到下一个键
   ```

   动作名:`newSession`、`sessionMenu`、`detach`、`deleteSessionMenu`、`agentList`、`overview`、`openShell`、`quit`、`splitPane`、`closePane`、`prevSession`、`nextSession`、`focusLeft`/`focusRight`/`focusUp`/`focusDown`、`focusNextPane`、`interrupt`(默认 `Ctrl+C`,连按两次退出)、`promptCommandMenu`(默认 `Ctrl+P`)。按键串的首键必须带 `Ctrl`/`Alt` 或是 F 键(否则会吞掉正常输入);同一按键串绑了多个动作、或一个按键串是另一个的前缀时,启动时记 WARN 日志并让冲突的自定义项回退默认。按下前缀后再按 `Esc` 或未绑定的键即取消。焦点在 shell 窗格时,`interrupt` 与 `promptCommandMenu` 的按键直达 shell,其余动作仍由 DSH 处理。
9. shell 工具:模型可按需选用以下几种 shell,各工具的提示词写明了彼此差异与常见坑。
   - `bash`:每次调用起一个新的 `bash -c`;Windows 上是 Git-Bash(不是 WSL)。
   - `pwsh`:每次调用起一个新的 PowerShell 7 进程,跨平台。
   - `bash_persistent` / `pwsh_persistent`:每个会话一个常驻 bash / PowerShell 7 进程,当前目录、变量、函数在多次调用间保留;`exit` 或超时会重置该进程。
   - `powershell`:仅 Windows 提供,调用 Windows PowerShell 5.1(`powershell.exe`),用于只在 5.1 上可用的模块或 COM。
   - `wsl`:仅 Windows 提供,在 WSL 发行版里执行 `bash -lc <命令>`;`distro` 参数选择发行版,缺省用 WSL 的默认发行版。未安装 WSL 或没有发行版时返回 wsl.exe 自身的提示与非零退出码;连 `wsl.exe` 都不存在时直接报错并给出安装方法(`wsl.exe --install`)。
   - 每个工具都是独立插件(`@deepseek-ai/dsh-tool-bash`、`dsh-tool-pwsh`、`dsh-tool-bash-persistent`、`dsh-tool-pwsh-persistent`、`dsh-tool-powershell`、`dsh-tool-wsl`),可用 `/plugins disable <包>` 单独关闭。
   - `safety.blacklist` 里的 shell 规则按方言生效:`bash:<命令>`(或 `bash_persistent:`、`wsl:`)同时拦截 `bash`、`bash_persistent`、`wsl`;`pwsh:<命令>`(或 `pwsh_persistent:`、`powershell:`)同时拦截 `pwsh`、`pwsh_persistent`、`powershell`。
10. MCP 资源:`settings.yaml` 的 `mcp:` 段配置了 MCP 服务器时,模型可用 `list_mcp_resources`、`list_mcp_resource_templates`、`read_mcp_resource` 三个工具读取服务器暴露的资源,`server` 参数填 `mcp:` 段里的服务器名。
   - 文本资源直接返回;超过工具结果落盘阈值(`storage.tool_results.max_bytes`,缺省 64 KiB)时只保留头尾,全文写入 `<home>/tool-results/` 下的文件并在结果里给出路径,模型可用 `read` 分段读取。
   - 二进制资源存入内容寻址附件库(`<home>/attachments/`),结果给出附件 id、本地路径与字节数;图片可再用 `read_image` 查看。
   - 列表工具不带 `cursor` 时取回全部分页,带 `cursor` 时只取该页并返回 `nextCursor`;只有不带 `cursor` 的完整列举才清除对应列表的变更提醒,资源列表与模板列表分别记账,列举其中一个不会清除另一个的提醒。资源模板(如 `notes://{name}`)需由模型填好变量后交给 `read_mcp_resource`。
   - 服务器未声明 resources 能力时三个工具直接报错说明;服务器推送资源列表变更后,模型会在下一次请求时收到提示并重新列举。旧协议接收会话通知,`2026-07-28` 起仅监听列表变更,不订阅单个资源内容。监听在活连接中结束或失败会显示错误,下一次 MCP 调用沿现有单次重连路径恢复,不会静默丢失后续通知。`/mcp` 会标出声明了 resources 的服务器。
11. 会话内定时提醒:直接让模型提醒你即可,例如「30 分钟后提醒我看 CI」「工作日每天 9 点提醒我写日报」。模型用 `schedule_create` / `schedule_list` / `schedule_update` / `schedule_delete` 管理当前会话的提醒;到点后提醒内容作为一条计划消息回到本会话:会话空闲时直接开始新一轮,正在回复时排在当前这一轮之后。
   - 时间规格(每个提醒选一种):`after_seconds`(正安全整数秒)、`at`(带偏移的绝对时刻,或本地日期 + 时间 + 时区)、`every_seconds`(安全整数秒,至少 60 秒)、`daily`(每天的本地时间)、`weekly`(每周的 ISO 星期几,周一 1 至周日 7,不可重复)、`cron`(按给定时区计算的五段 Vixie cron)。本地日期严格写 `YYYY-MM-DD`,时间必须包含秒:`HH:mm:ss`,可带 1–3 位小数秒,返回值统一为 `HH:mm:ss.fff`。绝对时刻写 `YYYY-MM-DDTHH:mm:ss[.fff]Z` 或数字偏移 `±HH:mm`,必须使用大写 `T`/`Z`,偏移小时范围 00–23、分钟 00–59,不接受 `-00:00`。日期、时间和时区不自动去除首尾空白;cron 允许首尾和字段之间的空白。
   - 时区只接受 `UTC` 或有效的多段 IANA 名称(如 `Asia/Shanghai`),不接受 Windows id、固定偏移或空白;系统提示给出的本机建议也会映射为 IANA/UTC,无法映射则建议 `UTC`。`daily`/`weekly`/`cron` 跳过夏令时中不存在的本地时刻,重复时刻只在较早的瞬间触发一次;`at` 指向不存在的本地时刻会被拒绝,重复时刻取较早的一次。
   - 提醒属于会话:会话关闭后不再触发;恢复该会话时提醒随之恢复,期间错过的一次性提醒补发一次,循环提醒只补发最近错过的那一次。fork 出的会话不继承提醒,子代理不能使用这些工具。`headless` 单次任务在回复结束后即退出,其中设置的提醒不会触发。
   - 提醒不是 goal:提醒消息不算用户输入,不会创建、恢复或完成 goal,也不改变 goal 的自动续跑。
   - 派发会先推进提醒状态,再排入会话收件箱。删除已派发的提醒不撤回排队消息;用户中断会清空收件箱,已派发的一次性提醒不会重投。禁用定时提醒插件后仍可读取带 `ignorable` 标记的会话事件,重新启用后恢复提醒状态;禁用期间不运行计时器。事件注册支持与其他会话的日志读写并发进行。
   - cron 严格限定五段:`分钟 小时 日期 月份 星期`。只接受数字、`*`、升序范围 `a-b`、步进 `*/n` / `a-b/n` 及其逗号列表;步长须为正安全整数,星期日可写 0 或 7。不接受名称、宏、`?`、`L`、`W`、`#`、倒序范围、单值步进 `a/n` 或秒字段。Vixie 的日期/星期 star 标记取决于该字段原文是否以 `*` 开头(包括 `*/n`):任一带 star 标记时两段都须匹配,两段均无 star 标记时任一匹配即可;完整数字范围不等同于 `*`,列表中 `*` 的位置也会影响标记。
   - 所有平台(包括 Windows invariant-globalization 发布档)均使用内置 TZDB 规则与时区别名映射,不依赖系统 tzdata 或 ICU,规则随应用版本更新。别名使用与 Intl 一致的稳定规范名(如 `Asia/Kolkata` 规范化为 `Asia/Calcutta`)。UTC 目标限定为 0001–9999 年且保留毫秒;规则没有可表示的未来目标时返回 `time_out_of_range`。
12. 网页搜索引擎:`web_search` 工具的 `engine` 参数选择搜索引擎,同一次调用里的所有查询都发给这个引擎;不填 `engine` 时走通用网页搜索。系统提示词会列出当前可用的引擎,每条搜索结果都标明回答它的引擎及其局限(只搜了哪类对象、限流额度)。

   | 引擎 | 插件包 | 搜索范围 | 凭据参数 | 未配置凭据时的限制 |
   | --- | --- | --- | --- | --- |
   | `deepseek-official`(默认) | `@deepseek-ai/dsh-web-search-deepseek` | 全网网页 | `apiKey`(必填) | 不可用 |
   | `github-repos` | `@deepseek-ai/dsh-web-search-github` | GitHub 仓库的名称、描述与 topic(查询里加 `in:readme` 可搜 README) | `token`(可选) | 每 IP 每分钟 10 次(配置后 30 次) |
   | `github-code` | `@deepseek-ai/dsh-web-search-github` | GitHub 默认分支上的文件内容 | `token`(必填) | 不可用(配置后每分钟 10 次) |
   | `stackoverflow` | `@deepseek-ai/dsh-web-search-stackoverflow` | Stack Overflow 的问题(标题、正文、标签) | `key`(可选,在 stackapps.com 注册应用获得) | 每 IP 每天 300 次(配置后 10000 次) |
   | `baidu` | `@deepseek-ai/dsh-web-search-baidu` | 百度全网网页引用(千帆/AppBuilder `baidu_search_v2`) | `apiKey`(AppBuilder API Key,需开通搜索) | 不可用;额度和计费以控制台为准 |
   | `google` | `@deepseek-ai/dsh-web-search-google` | Google Programmable Search Engine 配置的语料 | `apiKey` 与 `searchEngineId`(cx),均必填 | 不可用;仅既有 API 客户可用,2027-01-01 停服 |
   | `bing` | `@deepseek-ai/dsh-web-search-bing` | Bing 公共 RSS 入口的首屏结果 | 无;可配 `market`(默认不传,由站点选择地区) | 无公开自动调用额度/SLA,可能限流或封禁 |
   | `duckduckgo` | `@deepseek-ai/dsh-web-search-duckduckgo` | DuckDuckGo 公共 HTML 入口的首屏链接与标题 | 无;可配 `region`(默认 `wt-wt`) | 无公开自动调用额度/SLA,可能要求人工验证 |

   各引擎插件独立发现并启用,各自在 `plugins:` 段写 `false` 即单独关闭;缺凭据的引擎不进入可用列表,显式调用时给出配置项提示。
   新增的四个引擎都是 named-only,不会抢占默认 DeepSeek 搜索;也可在 `plugins."@deepseek-ai/dsh-web".searchProvider` 显式设为默认。
   凭据只从 `settings.yaml` 读取,错误只展示不含查询串的端点;请求、凭据与原始响应不会写入搜索日志:

   ```yaml
   plugins:
     "@deepseek-ai/dsh-web-search-github":
       token: <GitHub token>          # 可选; 开启 github-code 并提高限流, 只需公开仓库只读权限
     "@deepseek-ai/dsh-web-search-stackoverflow":
       key: <Stack Exchange key>      # 可选; 提高每日配额
     # "@deepseek-ai/dsh-web-search-stackoverflow": false   # 单独关闭 Stack Overflow 引擎
     "@deepseek-ai/dsh-web-search-baidu":
       apiKey: <AppBuilder API Key>
     "@deepseek-ai/dsh-web-search-google":
       apiKey: <Google API key>       # 仅既有 Custom Search JSON API 客户
       searchEngineId: <cx>
     "@deepseek-ai/dsh-web-search-bing":
       market: zh-CN
     "@deepseek-ai/dsh-web-search-duckduckgo":
       region: cn-zh
     # "@deepseek-ai/dsh-web-search-duckduckgo": false
   ```

   传输、代理与错误语义由所有站点引擎共享:统一读取 `network.outbound_proxy.url`/`bypass`,不另建绕过代理的 handler;代理在当前进程共享,修改后后续请求使用新路由。
   HTTP 429/配额错误返回 `WEB_PROVIDER_ERROR`,保留 `Retry-After` 提示;人工验证、重定向及无法识别的响应也报错,不伪装成“没有结果”,不自动换引擎。
   百度最多取 50 条网页引用;Google 每次最多 10 条、只能访问前 100 条,默认每天 100 次免费请求,开通计费后最高每天 10000 次,具体范围由 cx 决定。
   Bing RSS 不是已于 2025-08-11 退役的 Azure Bing Search API,没有总数或翻页保证,结果相关性可能与交互网页不同;其 RSS 条款限制个人非商业 RSS 阅读用途,其他用途需 Microsoft 许可。
   DuckDuckGo 不提供完整网页搜索的公开 JSON API,这里使用非 JavaScript HTML 入口而不是只有百科摘要的 Instant Answer API;遇到站点验证需人工处理或由用户选择其他引擎,不会自动解验证码。
   入口与限制依据:[百度搜索 API](https://ai.baidu.com/ai-doc/AppBuilder/pmaxd1hvy)、[Google 当前可用范围与停服说明](https://developers.google.com/custom-search/v1/overview)、[Bing API 退役公告](https://learn.microsoft.com/en-us/lifecycle/announcements/bing-search-api-retirement)、[Bing 公共 RSS](https://www.bing.com/search?q=dotnet&format=rss)、[DuckDuckGo HTML](https://html.duckduckgo.com/html/?q=dotnet)。
   注意 `/plugins disable|enable` 会丢弃该插件的参数,重新启用后需要再写一次凭据与地区配置。

13. 工作目录与 worktree:模型可用 `working_directory` 查看或切换本会话的工作目录(`cd`),用 `create_worktree` 从本地 commit/branch/tag 新建 Git 分支与 worktree(位于 `<当前检出根>/.agents/worktrees/<名字>`)并切换进去。
   - 切换只影响之后的相对路径解析(包括 shell 的相对 `workdir`),以及新启动的 shell/终端的默认目录;已经存在的 `bash_persistent` / `pwsh_persistent` shell 与终端仍停在各自的目录。
   - 写权限根固定为会话的初始目录,不随切换移动:写入权限根之外的文件(`write`/`edit`/`str_replace_editor`)、在权限根之外启动 shell/终端或建 worktree,都会先弹审批,提示「目标路径在权限根之外」;审批策略为 never 时直接拒绝。
   - 两者分别对应插件 `@deepseek-ai/dsh-tool-working-directory` 与 `@deepseek-ai/dsh-tool-worktree`,可用 `/plugins disable` 关闭。
14. A2A 宿主默认只监听回环地址；自动端口由实际 HTTP 监听器直接绑定并持有，连接时使用宿主报告的 `Endpoint`。指定端口已被占用时直接报错，不暗中换端口或重试；对非回环地址开放时必须配置认证令牌。关闭宿主会释放监听端口。

## 图形界面

GUI 是内置插件 `@deepseek-ai/dsh-gui`,与 TUI 共用同一份配置、会话与命令实现(`/` 命令、`@` 引用、审批、`ask_user_question` 都走 harness 的同一条链路)。
普通本地 Chat 也由常驻 Host 创建、执行与持久化，GUI 只是客户端；总览、浏览器和 `dsharp session list` 看到同一会话，退出 GUI 不会中止正在执行的回合。
原有 `sessions/` 历史会话可直接打开，恢复时由 Host 接管，不会另建一份 GUI 私有会话。

```bash
# 构建 GUI 启动器(独立于 CLI/TUI 宿主;产物在 Dsh.GuiHost/bin/<Debug|Release>/net10.0/)
dotnet build Dsh.GuiHost/Dsh.GuiHost.csproj
# 根目录的 dotnet build 会经 DeepSeek-Harness-Sharp.slnx 一并构建它

# Windows: 双击 dsh-gui.exe(Windows 子系统,不会出现控制台黑框),或从命令行启动
Dsh.GuiHost\bin\Debug\net10.0\dsh-gui.exe
# Linux/macOS: 运行同一工程的产物
Dsh.GuiHost/bin/Debug/net10.0/dsh-gui
# 从控制台宿主进入 GUI(会占用当前控制台)
DeepSeek-Harness-Sharp\bin\Debug\net10.0\dsharp.exe gui
# 直接打开某个历史会话
DeepSeek-Harness-Sharp\bin\Debug\net10.0\dsharp.exe gui --session <会话 id>
```

- 挂到桌面环境:Windows 用 `pwsh -File scripts/install-desktop.ps1`(在桌面创建快捷方式),Linux 用 `bash scripts/install-desktop.sh`(写入 `~/.local/share/applications/dsh-gui.desktop` 并复制图标,GNOME/KDE 等桌面可直接固定到 dock/面板)。
- 快捷键:`Enter` 发送、`Shift+Enter` 换行、`Esc` 回到对话、`Ctrl+N` 新会话、`Ctrl+B` 折叠左栏、`Ctrl+1`/`Ctrl+2` 对话/轨迹、`Ctrl+,` 设置、`Ctrl+W` 关闭(按关闭行为)、`Ctrl+Q` 直接退出。
- 插件市场页:安装框旁的文件夹按钮可浏览选择插件库文件(`.dll`/`.so`/`.dylib`);每个插件有启用/禁用、"移除"按钮(与 `/plugins remove` 相同,托管插件移除后照常校验加载上下文回收)以及读/写/网络三个权限勾选框,写入与 `/plugins ban_write|enable_write` 相同的 `settings.yaml` 键,两端看到同一份状态。
- 设置页的外观、窗口与 GUI 参数仍保存在 GUI 插件自己的参数段(写在 `settings.yaml` 的 `plugins:` 里,不会新建 `ui:` 顶层段):

  ```yaml
  plugins:
    "@deepseek-ai/dsh-gui":
      theme: dark           # dark|light|system
      fontSize: 13.5        # 11~18
      closeAction: tray     # tray|quit|ask
      workspaceView: solution   # solution|filesystem
      sidebarVisible: true
      gpu: { enabled: true, adapter: auto, backend: auto }   # adapter 选显卡(设置页会列出本机识别到的卡), backend 是高级项: auto|opengl|vulkan|software,改动重启生效
      window: { rememberBounds: true }
  ```
- 「网络」管理本机基准出口与本机借用选择；「远程」管理机器连接及目标 Host 的出口，两者不会搬动 GUI 插件参数。

- 内容折叠:思考、工具调用与结果、上下文注入默认折叠,点「▸ 展开 / ▾ 折叠」切换;轨迹条目用右侧箭头展开详情;助手正文中的代码块超过 12 行先折叠。
- 对话跟随:位于底部时自动跟随新增消息、流式正文和窗口尺寸变化；向上滚动后保持阅读位置，回到底部再恢复跟随。
- 需要用户决定:工具审批与 `ask_user_question` 共用同一个窗口。审批提供「仅本次允许(y)/总是允许(a,本会话内该工具不再询问)/拒绝(n)/取消(c 或 Esc)」,并展示工具的主参数与影响提示。
- 会话标题:首条用户消息写入会话标题(会话 id 不变),侧栏会话项的 `⋯` 菜单可重命名、导出日志、删除。
- 消息操作:助手消息下方有复制、有帮助/没帮助、重新生成(把最后一条用户消息重新入队)。
- 交付物:模型用 `present` 声明的文件列在会话顶栏「交付物」下拉里,可「打开」(系统默认程序)或复制路径;远端会话只列出、不在本机打开。
- 单实例:同一份 home 只会有一个 GUI,重复启动(双击、快捷方式、`dsh-gui.exe`)只会把已有窗口调到前台,不会开第二个窗口。同一个 home 里同时跑 GUI 与 CLI/TUI 也是允许的,日志会自动共用同一个文件。
- 显卡选择:设置页「图形与加速」列出本机识别到的显卡(Windows 读显示类驱动注册表,Linux 读 `/sys/class/drm`),默认「自动」。Windows 上通过 Avalonia 的显卡选择回调按名字匹配;Linux 上用 PRIME 选择器(`DRI_PRIME` / `__NV_PRIME_RENDER_OFFLOAD`)表达偏好,部分驱动或容器里可能不生效。保存后重启生效;想确认实际用了哪张卡,Windows 上可在任务管理器或 `nvidia-smi` 里看 `dsh-gui.exe`。
- 关闭行为:默认「最小化到托盘」,设置页「图形与加速」可改成直接退出或每次询问。Windows 用通知区域图标;Linux 走 StatusNotifier,KDE 自带托盘宿主,GNOME 需要 AppIndicator 扩展(Ubuntu 桌面默认带)。启动时若探测不到托盘宿主(例如 GNOME 没装扩展),会按「直接退出」处理,避免窗口藏进看不见的地方。
- 平台说明:自绘标题栏、显卡选择与托盘在 Windows 上支持最完整。在 Linux(含 WSL) 上后台验证界面可用 `bash scripts/verify-gui-linux.sh`(Xvfb 虚拟显示 + 截图,不打扰当前桌面);托盘行为可用同样方式起 plasmashell / gnome-shell 后验证(见 AGENTS.md 规则 55)。

## 多机多会话

一个客户端同时管理本机与多台 SSH 机器上的会话:harness 会话、外部 Agent(Codex、Claude Code、OpenCode 及任意 ACP Agent,需在目标机器上已安装对应 CLI)与终端。
ACP 的本地 socket 与外部 Agent 进程共用 `Dsh.Acp.AcpClient`、`AcpMethods` 和 `Dsh.Sdk.JsonRpcLineTransport`，不另设 stdio 客户端或协议模型。
权限请求等待人工回答时仍可接收后续更新、并发请求和响应；同一请求只接受第一个有效回答，取消回合或连接关闭会释放待决权限。
客户端的文件与终端能力保持关闭，OpenCode 继续使用自身配置的模型和工具；传输写入完整 NDJSON 帧，协议损坏或写入失败会结束连接并使待决调用失败，不自动重试。
机器清单写在 `settings.yaml` 的 `fleet:` 段,GUI、命令行与浏览器共用这一份:

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
    autoDeploy: true       # 连接前在远端安装/升级 dsharp
```

```bash
dsharp machine list | add | remove | test | deploy    # 管理机器(dsharp machine add build --host build.internal --user dev --auth key --key ~/.ssh/id_ed25519)
dsharp session list [--machine build]                 # 列出所有机器上的会话
dsharp session attach build/<会话 id>                  # 接入终端会话,Ctrl+] 离开
dsharp session stop build/<会话 id>                    # harness 中断当前回合；terminal、ACP、外部 Agent 结束进程
dsharp doctor [--machine build]                        # 逐项诊断连接,并给出可复制的修复命令
dsharp host serve                                     # 手动前台运行 Host; GUI/SSH 连接也会按需启动后台 Host
```

**配置目标 Host 的网络出口**

已保存的本机或 SSH 机器可直接管理目标 Host 的 `network.outbound_proxy`：

```bash
dsharp machine proxy build show
dsharp machine proxy build set --json-stdin < outbound-proxy.json
dsharp machine proxy build test --probe-url https://www.gstatic.com/generate_204
dsharp machine proxy build clear
```

`outbound-proxy.json` 为 JSON 对象，例如 `{"url":"http://127.0.0.1:7890","bypass":["example.com"],"probeUrl":"https://www.gstatic.com/generate_204"}`；需要认证时也只通过 stdin 提交 URL，不把凭据放入命令参数。代理地址和探测地址均由目标 Host 访问，`127.0.0.1` 指目标机器而非 CLI 所在机器。
输入文件含凭据时，将它纳入 `.gitignore` 或放在仓库之外，不要提交或上传。

`show`、`set`、`clear` 输出脱敏 JSON 状态，包含目标 Host 身份、home 与修订号；`test` 输出可读的脱敏探测结果，连通时退出码为 0，否则为非零。`set` 和 `clear` 先读取当前修订号，并发修改会明确报冲突，不自动覆盖或重试。`clear` 删除目标的整个 `outbound_proxy` 子项，保留其它网络设置；探测不会保存或改变已有路由。

这些命令借用 Fleet 的机器连接，不保存远端出口副本，不修改 CLI 所在机器的代理，也不自动部署或升级目标。目标 Host 必须提供 `network.outbound` 能力；旧 Host 会明确提示需要升级。连接断开时命令失败，不等待未来重连。

**按层诊断网络**

```bash
dsharp doctor --proxy-layer ssh --machine build
dsharp doctor --proxy-layer local_outbound
dsharp doctor --proxy-layer remote_outbound --machine build
```

`ssh` 只执行真实 SSH `true`，不部署软件，也不要求目标运行 Host 或能访问 HTTP。
`local_outbound` 只检查当前 home 的基准 `network.outbound_proxy`，不使用借用出口，也不受 `--machine` 影响；借用出口仍用下方的 `machine tunnel <name> test` 单独检查。
`remote_outbound` 通过所选目标现有 Host 的 Network API 发起 HTTP，不配置或替代本机出口；需要支持可取消结构化探测的 Host，旧 Host 明确返回 `Unsupported`，不冒充通过。

每个目标输出一行 JSON，只含所选层的结果；字段包括 `Layer`、`Outcome`、`Stage`、`Detail`、可空 `HttpStatus`、机器与 home，以及本次 `OperationId`。
`Passed` 退出 0，`Failed`/`Unsupported` 退出 1，用户取消且所属资源完成回收后为 `Cancelled`、退出 130。
HTTP 403/404 等目标响应只表示出口可达，不代表业务成功；HTTP 407 是 `proxy_auth` 失败。
SSH 探测预算为 15 秒，代理 TCP 连接 5 秒、HTTP 请求 15 秒，本机合计 20 秒，远端探测 35 秒；远端取消另用最多 5 秒确认所属请求已回收，无法确认则明确失败。
检查不保存草稿、不切换路由，也不关闭其他消费者；不自动重试，SSH 层仅发布安全说明，不将 OpenSSH 原始 stderr 或密钥路径写入该探测结果。
JSON 的 home 等身份字段仍可能包含本机路径，分享输出前需额外隐藏用户名、路径和私有地址。
不带 `--proxy-layer` 时仍使用原来的综合 `doctor`。

- GUI:启动后进入总览,按 机器 → 项目 → 会话 分组,等待你审批的会话会高亮;`Ctrl+K` 快速跳转,`Esc` 返回;铃铛是通知中心,所有机器的待审批与提问都能在这里直接回答,先答者生效。侧栏还有文件、Git、机器三页;「设置 → 远程」与机器页是同一份机器清单。
- 审批和提问由 Host 的单一待决队列拥有；`host.pending.changed` 在订阅时及每次变更后推送完整投影，结局可附首答者审计，所有客户端据此关闭已处理的对话，不保存已解决请求的记忆缓存。等待高亮只来自真实待决请求（包括 ACP 结构化权限请求）；外部终端仅显示进程状态，不再用屏幕文本或正则猜测审批、忙碌和等待。
- 断线后每台机器独立重连,会话事件从断点续订,终端会重新接入。
- 本机按需启动和重连先在有界探测内验证 Host 握手，成功后直接复用连接；只接受 socket 而不回应握手的端点不算已就绪，认证和协议拒绝仍直接报错。
- 机器收到已提供能力的初始会话、待决和通知快照后才显示已连接；初始化期间的心跳失败会结束该次尝试，按配置显示离线或重连中，启用自动重连时再重试。
- Host 严格使用线协议版本 2；版本不一致时握手拒绝，不再兼容旧宿主的缺失方法、resize 空操作或按错误文本恢复会话。核心包括会话执行与事件、登记表和唯一待决队列；Agent、PTY、文件、Git、通知、会话编辑、模型目录和插件管理按领域组合。握手与 `host.info` 只声明该 Host 实际提供的能力，未提供的领域调用明确返回 method-not-found，不返回伪造的空列表或成功。客户端按能力显示可用操作；协议目录列出的已知能力不表示每个 Host 都支持。
- C# 集成端的 `IRemoteHost` 只暴露核心会话和领域入口；文件/Git 等操作分别通过 `IRemoteFiles`、`IRemoteGit` 等领域接口调用，不再提供核心客户端上的平铺转发方法。会话登记与通知投影按序可靠排队，初始快照和后续更新不会因观察队列达到固定条数而被丢弃。
- 会话订阅和 PTY attach 在异步获取后端读者之前就登记独立 reader ID；初始化期间退订或分离也会释放迟到取得的事件监听/daemon 订阅，不影响同一会话的其它视图，也不终止会话本身。未知会话或 PTY 的初始化错误直接返回该 RPC，不提前确认成功。
- ACP 对话使用与 harness 相同的会话持久化、查询和事件订阅，Host 重启或恢复 agent 时保留原会话 ID、回合与工具事件。
  已停止对话的历史按需读取，查看历史不会启动外部 agent；继续对话时才重新启动进程。
  启动命令、参数与环境随 Host 登记以仅属主可读写的权限保存，公开会话登记和状态推送不包含这些字段。
- 通知与已读标记只保存为 `host/notifications.json` 原子快照，跨 Host 重启保留；不维护单独的追加日志、压缩、保留期或 JSONL 导入引擎。
- 浏览器：先单独安装 `dsharp-web`（见下方发布形态），再运行 `dsharp web` 或 `dsharp-web`；`dsharp host web` 也是同一进程转发入口，TUI/GUI 不加载 WebBridge 或 ASP.NET。独立 WebHost 内的真实 WebBridge 插件拥有 Fleet 连接、协议桥和监听生命周期，只监听 `127.0.0.1:8780`；`dsharp web --print-token`（或 `dsharp-web --print-token`）打印登录链接，其它设备先 `ssh -N -L 8780:127.0.0.1:8780 user@host` 转发再访问。
  独立安装的 WebHost 可按需启动同一可执行文件内的 Host 与 PTY daemon，无需另装 TUI 可执行文件。
- 手机触摸或触控笔点按终端会取得输入焦点，不阻止正常滚动手势；首屏快照解析完成后才解除忙碌标记，使用终端键栏发送 `Ctrl+C` 后仍可继续输入。文字、回车和键栏按键按各自终端的输入顺序提交，切换页面不会把已排队的输入发送到新会话；发送失败会显示错误且不自动重放。
- `@deepseek-ai/dsh-fleet` 提供机器客户端服务并拥有客户端连接的生命周期，禁用时断开客户端而不终止 Host 会话；可复用连接与部署实现位于 `Dsh.Fleet.Client`，GUI、WebBridge 和 CLI 共用它。
- SSH 的认证（agent/key/password、askpass）、ProxyCommand、进程管道及上传由 `SshTransport` 独占；部署策略通过 `IRemoteMachineTransport` 决定版本复用、远端下载或本地上传，doctor 也只消费该传输边界。Host 协议握手不再公开未握手的 raw Web/隧道入口；杀掉 SSH 桥接仍不结束常驻 Host 和原 PTY。
- Unix 密码认证的临时 askpass 脚本从创建起只授予当前用户权限；Host 握手成功即删除脚本，SSH 桥接仍保持连接，失败和退出时也会清理。
- `@deepseek-ai/dsh-web-bridge` 拥有 WebUI 入口及桥接服务器生命周期，停用或退出入口会停止监听并删除临时访问凭据；它不是绕过插件装载的静态网页宿主。
- 浏览器只渲染服务器下发的权威视图、会话事件和实际 PTY 输出，提交文件/Git/会话/审批等操作；机器重连、能力处理、按序续订和 PTY 重挂由与 GUI/CLI 共用的 Fleet/RemoteHost 提供，浏览器不保存第二套事件回放或已解决请求缓存。关闭发起页面不会终止 Host 里的会话。
- GUI 文件树支持轻量编辑、目录操作与可取消上传下载；Web 文件页保留只读预览与下载。两者的远端操作都由同一 Host 文件服务执行：允许路径仅限用户 home、harness home 与已注册会话的项目目录，符号链接也由 Host 检查，客户端不能通过输入路径绕过限制。
- 多个浏览器共享 Fleet 连接，但未提交上传仍归创建它的浏览器所有：关闭页面会取消其临时上传，包括尚未读到 begin 响应的上传，不影响另一页面继续分块或提交。取消单个会话/终端视图只停止该视图，不取消共享 WebSocket 上已经开始发送的帧。
- GUI 本地终端与 SSH 终端都由 Host/PTY daemon 托管；附加到已有会话的终端页关闭时只分离客户端，自己创建的终端页则停止自身 PTY。会话退出会取消尚未连上的额外 shell 启动，并等待已发出创建请求的自有资源回收，不影响原会话或其他客户端。重接会恢复主屏/备用屏、光标、滚动区域、换行模式及 DEC 图形字符集，`tmux` 切窗和切 pane 不再共用错误的屏幕保存槽，窗格边框不会因字符集丢失变成字母；离线期间最新窗口尺寸在重新 attach 前应用。
- GUI 新建会话时，若选中机器尚在连接，会等待该连接完成后显示可用 Agent，无需关闭窗口或重新选机器；切换机器会取消旧机器的目录读取，关闭窗口也会取消尚未完成的读取，不断开其他消费者或停止 Host。
- 终端的非独占订阅也可在进程退出后读取最终屏幕并正常结束；瞬时结束的命令仍正常入册，不因 Host 建立观察订阅稍晚而误报启动失败。
- 终端聚焦时 `Ctrl+B`、`Ctrl+K`、`Esc` 等按键原样送往 PTY，不再被窗口快捷键先行截获；点击终端外的导航区后再使用工作台快捷键。普通 Chat 重新显示或重新挂载时会恢复输入框焦点及事件订阅。
- 后台工具、Git、浏览器和剪贴板命令不会继承活动终端的输入；持续协议保留自己的输入管道，Unix 信号打断读取后仍可继续输入。
- TUI Git 面板与 GUI/Web 的 Git 检查共用 `Dsh.GitInspect`：面板仅展示排序、截断后的结构化统计；刷新使用单次 `--raw --numstat -z` diff，不请求 patch，也不逐文件启动 Git。暂存与未暂存改动一起对比 HEAD，干净仓库显示上次提交；首次提交按仓库对象格式计算空树，重命名、二进制及含制表符/换行的文件名由同一解析器处理。GUI/Web 的 status、逐文件 diff、log 和 worktree 管理保持不变。
- 这里继续使用 Git CLI，而未引入 [LibGit2Sharp](https://github.com/libgit2/libgit2sharp)：它能省去进程启动和机器输出解析，但会增加托管绑定、原生 libgit2 资产及各发布 RID 的加载/NativeAOT 验证，并需重新核对既有 Git 配置、属性/textconv、重命名和 worktree 行为；不能仅因是托管 API 就认定 AOT 发布已兼容。当前 summary 已去掉逐文件进程与 patch 生成，保留安装 Git 的既有执行边界和统一解析实现，不额外维护第二套后端。
- 同一 home 只允许一个常驻 Host 持有会话注册表，SSH、GUI 与 CLI 自动启动共用就绪等待；后台启动进程仍存活时继续等真实监听，退出或取消时结束等待，普通端点连接保留有界探测，避免冷启动被误判为不可达。
- 机器清单与部署偏好只读写 `settings.yaml` 的 `fleet` 段；不读取或自动导入 `fleet/machines.json`、旧 GUI 插件的 `remoteWorkspaces` 或私有代理配置。试用过这些布局的用户需在机器页、CLI 或 `fleet` 段显式重新添加配置；原文件和旧插件参数不会因读取配置而被改写或删除。
- 本机下载发布包使用与当前 home 相同的有效出口：已选择借用机器时建立独立的短期借用租约，否则使用 `network.outbound_proxy`；取消或下载结束只回收本次资源。旧 GUI/Fleet 私有的 `downloadProxy` 不自动迁移。
- `fleet.deploy.serverProxy` 仅用于在远端下载部署包，不是每台服务器的一般网络出口；SSH 的跳板/代理命令仍配置在机器条目的 `proxy`。

```yaml
network:
  borrowed_outbound_machine: null
  outbound_proxy:
    url: http://127.0.0.1:7890  # 留空跟随系统代理
    bypass: [example.com, "*.example.org"]
```

### 通过 SSH 借用远端代理

已保存的 SSH 或 SSH-over-iroh 机器可配置独立的 `outboundTunnel`，通过 `proxyCommand` 的受管 `ssh -T -W` 字节流，或 `localForward` 的受管 `ssh -N -L` 本地转发访问远端 TCP 代理。
远端只需可用的 SSH 和代理，不要求安装 `dsharp` 或启动 Host；这与设置目标 Host 自身出口的 `machine proxy` 命令不同。

保存 `outbound-tunnel.json`，内容例如：

```json
{"mode":"proxyCommand","proxyUrl":"http://127.0.0.1:7890"}
```

需要本地监听时，可将同一文件写为：

```json
{"mode":"localForward","proxyUrl":"http://127.0.0.1:7890","localPort":17890}
```

```bash
dsharp machine tunnel build set --json-stdin < outbound-tunnel.json
dsharp machine tunnel build show
dsharp machine tunnel build test --probe-url https://example.com/
```

PowerShell 中保存时使用 `Get-Content -Raw .\outbound-tunnel.json | dsharp machine tunnel build set --json-stdin`。
`proxyUrl` 从远端视角解析，`127.0.0.1` 指该 SSH 机器；支持 HTTP、HTTPS、SOCKS4/4a/5，并保留代理的原始主机名、TLS 校验和认证。
含认证信息的 URL 只通过 stdin 输入，输入文件应仅自己可读并加入 `.gitignore`，不要放进命令参数、提交或评论。

配置保存在 `fleet.machines[].outboundTunnel`，也可直接填写 `mode` 和 `proxyUrl`；两项必须同时有效。
本机机器不接受此配置；`proxyCommand` 不接受 `localPort`，`localForward` 的 `localPort` 可省略或设为 1 至 65535。
它不会修改 `network.outbound_proxy`、机器的 SSH `proxy`、`fleet.deploy.serverProxy` 或其他机器；`clear` 只删除该机器的 `outboundTunnel`。
保存配置本身不会将 TUI、GUI 或其它请求切换到借用出口；这里的 `test` 是独立验证操作，只释放本次取得的连接与租约，不切换当前出口。

`localForward` 仅监听客户端的 `127.0.0.1`，逻辑代理主机名、认证和 TLS 身份仍取自 `proxyUrl`。
省略 `localPort` 时向 OS 申请一次候选端口；无关程序占用固定端口或候选交接冲突时明确失败，不抢占、不杀占用者，也不循环更换端口。
若固定端口已由当前受管借用持有，独立验证会明确说明并使用临时本地端口，不写回配置、不替换或关闭当前借用。
临时端口、SSH PID 和 askpass 不写入配置。
同一路由代际内复用一个受管 SSH 进程，每个 TCP 连接独立释放，取消一个请求不会关闭其他消费者的连接。

`test` 实际发送 HTTP(S) 请求并消费响应正文，输出脱敏状态，成功状态退出码为 0，连接、认证、TLS 或请求失败时为非零。
自检目标不能被直连规则排除；正常请求仍保留既有 bypass 和安全校验，进入借用路由后失败不会回退或重放原请求，SSH 掉线后仅下一次新请求可重建。
TCP HTTP/1.1 和 HTTP/2 可用，借用出口强制 HTTP/3 会明确报不支持；取消或命令退出会等待自有 SSH、流及 askpass 清理，不会停止目标 Host 或 PTY。
受管隧道保留 SSH 的 `LocalCommand`/`PermitLocalCommand` 来识别目标认证完成，`sshOptions` 不得显式设置这两项，SSH 配置文件中的同名命令也不会用于该隧道；普通 Host 连接保持原有认证行为。
`localForward` 同时固定 `ForkAfterAuthentication=no` 和 `ClearAllForwardings=no`，保持所属进程在前台并保留受管监听；用户选项或 SSH 配置文件不能覆盖这两项。

配置好机器后，显式选择当前 home 的共享借用出口：

```bash
dsharp machine tunnel build use
```

TUI 中使用 `/proxy via build` 选择机器，`/proxy direct` 清除选择，`/proxy reload` 重新读取当前 TUI 的配置。
机器名中的空格可直接跟在 `/proxy via` 后面。
CLI 与 TUI 共用 `settings.yaml` 的 `network.borrowed_outbound_machine`；默认 null 仅使用基准本机出口，不建立 SSH。
选择前会验证机器、隧道和探测请求；验证或保存失败保留原选择和路由，已保存的无效借用选择在实际本机 HTTP 消费时明确失败，不暗中退回基准或另一台机器。
基准的 `/proxy set`、`/proxy clear`、`/proxy test` 仍只操作或检查 `network.outbound_proxy`，不会解除借用选择。
GUI「设置 → 网络」的「验证并保存借用」验证所选机器后保存同一个共享选择；「清除借用」恢复基准出口，「仅验证借用（不保存）」不改变选择。
本机代理与探测地址默认遮蔽凭据，点击「编辑代理与探测地址」才展示完整草稿；未编辑时保存仍保留原值，不会把遮蔽文本写回配置。
检查草稿不会保存配置，取消检查、离页或切换机器后不会接受迟到结果。

```bash
dsharp machine tunnel direct
dsharp machine tunnel build clear
```

`direct` 只清除共享选择；随后可用 `clear` 删除已不需要的机器隧道定义。
不要用删除隧道定义代替清除仍在使用的选择。
共享的是同一 home 的选择，不是跨进程共享连接：各 Context 独立持有运行期租约，同一 Context 的消费者共用该代路由。
同一 home 的配置保存协调进程间的完整读取、修改和替换，避免借用选择与基准出口等不同配置项的并发保存互相覆盖；不同 home 独立保存。
切换、重载、最后消费者退出或卸载会关闭旧代自有连接、SSH 与 askpass，不会结束目标 Host、后台会话或 PTY，也不会重放已经发出的请求。
本机 Host 是独立消费者，需要支持 `network.reload` 的连接客户端单独请求它重读自身文件；不会接收客户端的完整网络配置快照。
仅提供 `network.outbound` 的旧 Host 仍可读取、保存和探测基准出口，但不支持此重载。
Server 下载仍只使用 `fleet.deploy.serverProxy`，不启用本机借用选择。
不需要本机下载的 SSH/远端 Host 管理不会获取本机出口租约；Client 下载或 Auto 回退本机下载时才按共享选择消费本机出口。


### 通过 iroh 接入没有公网 SSH 地址的机器

普通 SSH 保持不变；选择 SSH-over-iroh 后，客户端通过稳定的 Endpoint ID 寻址，能直连时直连，否则使用可达的 relay。
SSH-over-iroh 是独立的可选程序集 `Dsh.Fleet.Client.Iroh`；基础 `Dsh.Fleet.Client` 不依赖它，正常产品包含接入支持，但不下载或捆绑原生 `iroh-ssh` 命令。
客户端必须由用户安装 `iroh-ssh` 并放入实际启动进程的 `PATH`，系统 OpenSSH 的 `ssh`、`scp` 仍需可用；产品只检测可执行命令是否存在，不把它当作版本兼容或网络就绪证明。
GUI 仅在检测到命令后显示新增 Iroh 机器的入口；缺少命令不会隐藏或改写已保存的 Iroh 机器，尝试连接或启用时会明确报告缺少依赖，不自动安装、下载或退回普通 SSH。
WebBridge 使用运行它的那台机器上的可选程序集和已安装命令，浏览器设备无需安装 iroh；浏览器到 WebBridge 的网络可达性仍需单独解决。

**先在客户端和接收目标准备命令：**

客户端需要支持 `proxy` 的兼容 `iroh-ssh`；接收目标还必须支持 `server --ready-file`，以保留地址发现就绪、失败回滚和接收端进程所有权语义。
上游固定源码修订 `82e99719f8b4e44c69622140f4acb25e5ae54184` 本身没有 `--ready-file`，不能只安装同名上游版本就认为接收端兼容。
本仓库的 `scripts/fetch-iroh-ssh.sh` / `.ps1` 是显式调用的开发与 CI 原生测试依赖准备脚本：它们校验该修订、应用 `scripts/iroh-ssh-readiness.patch`，产出可供用户自行安装的兼容命令及许可说明；普通 build/publish 不调用它们。
以下在仓库根运行，需要 Git、Rust 的 `rustup` / `cargo` 及平台原生编译器；Linux/macOS 还需要 Python 3、curl、tar、jq，Windows 需要 PowerShell 7 和 Visual Studio C++ 工具链。
已有兼容命令时无需重复安装；下面操作会更新专用 `dsharp-iroh` 安装目录中的文件。
若同名命令由其他安装管理，先自行选择保留或调整安装位置，示例不会覆盖它。

Linux x64 示例（macOS Apple Silicon 把 `RID` 改成 `osx-arm64`，其他架构使用脚本支持的对应 RID）：

```bash
(
set -e
RID=linux-x64
install_dir="$HOME/.local/lib/dsharp-iroh"
link_path="$HOME/.local/bin/iroh-ssh"
if { [ -e "$link_path" ] || [ -L "$link_path" ]; } && [ "$(readlink "$link_path" 2>/dev/null)" != "$install_dir/iroh-ssh" ]; then
    printf 'Refusing to replace an existing iroh-ssh installation at %s\n' "$link_path" >&2
    exit 1
fi
bash scripts/fetch-iroh-ssh.sh "$RID"
mkdir -p "$install_dir" "$HOME/.local/bin"
cp artifacts/iroh-ssh/"$RID"/{iroh-ssh,LICENSE.txt,THIRD-PARTY-LICENSES.txt,NOTICE.txt,READINESS.patch} "$install_dir/"
if [ ! -L "$link_path" ]; then
    ln -s "$install_dir/iroh-ssh" "$link_path"
fi
export PATH="$HOME/.local/bin:$PATH"
iroh-ssh version
iroh-ssh server --help
dsh-gui
)
```

Windows x64（在 PowerShell 7 中运行；ARM64 使用 `win-arm64`）：

```powershell
$rid = 'win-x64'
pwsh -NoProfile -File scripts/fetch-iroh-ssh.ps1 -Rid $rid
$install = Join-Path $env:LOCALAPPDATA 'dsharp-iroh'
New-Item -ItemType Directory -Force $install | Out-Null
'iroh-ssh.exe', 'LICENSE.txt', 'THIRD-PARTY-LICENSES.txt', 'NOTICE.txt', 'READINESS.patch' |
    ForEach-Object { Copy-Item "artifacts/iroh-ssh/$rid/$_" $install -Force }
$env:PATH = "$install;$env:PATH"
iroh-ssh version
iroh-ssh server --help
dsh-gui
```

检查 `server --help` 中确有 `--ready-file`；命令存在但不兼容时，接收端启用仍会明确失败。
以上 `PATH` 修改仅作用于示例的执行环境及其子进程；从该环境启动 `dsh-gui` 可让 GUI 继承它，从桌面图标、Finder 或服务启动则需在对应登录/启动环境配置同一目录并重新启动应用。
不要只修改交互式 shell 的 `PATH` 就假定桌面 GUI 或远端非交互 SSH 同样可见。
在接收目标重复相应安装步骤，使目标本地的 `dsharp iroh-remote enable` 以及远端非交互 SSH 启动的 `dsharp` 都能通过其 `PATH` 找到兼容命令；已有 SSH 自动启用只部署接收端产品，不替目标安装原生命令。

**已有一次可达的 SSH 连接：**

```bash
dsharp machine add home --host 192.168.1.20 --user dev --auth key --key ~/.ssh/id_ed25519
dsharp machine enable-iroh home
dsharp machine test home
```

GUI 中添加或选择普通 SSH 机器，然后点击「通过现有 SSH 启用 Iroh」。
客户端单独部署接收端产品（目标端兼容原生命令须预先安装），通过真实 iroh SSH 命令验证认证与身份后才保存转换后的机器配置，不替换正在运行的 Host。
失败或取消保留原 SSH 配置；本次新启动的接收端会回滚，已有运行中的接收端只复用、不擅自重配。
如果 SSH 本身已经断开而无法确认回滚，错误会明确要求在目标端运行 `dsharp iroh-remote disable`，不把清理失败当作成功。

**客户端完全无法 SSH 到达目标：**

先通过本机操作、云控制台或其他已有方式，把包含可选接入支持的发行包放到目标机器，完成上面的兼容命令安装与 `PATH` 配置后运行：

```bash
dsharp iroh-remote enable                         # 默认连接目标本机 127.0.0.1:22 的 sshd
dsharp iroh-remote info                           # 显示 endpointId、运行状态和后台服务说明
dsharp iroh-remote disable                        # 停止接入，保留身份、原有 sshd 和 Host 会话
```

自定义 sshd 端口使用 `dsharp iroh-remote enable --ssh-port 2222`。
未检测到本机 SSH 服务时会给出对应平台的安装或启用指引，不自动提权、改防火墙或安装另一套 SSH 服务。
在客户端选择「新增 Iroh 机器」，填写目标端显示的 Endpoint ID、SSH 用户和认证信息；命令行等价入口为：

```bash
dsharp machine add home --endpoint-id <endpoint-id> --user dev --auth key --key ~/.ssh/id_ed25519
dsharp machine test home
```

Endpoint ID 不是登录密码；密码、密钥和 ssh-agent 认证及 SSH 主机密钥校验继续生效。
SSH-over-iroh 使用独立的主机密钥别名，不借用普通 TCP SSH 的已建立连接；接收端离线或认证失败时不静默退回原 IP。
私钥保存在目标 harness home 的 `iroh-receiver/identity/` 中，备份和恢复时保留整对身份文件；删除它们会改变 Endpoint ID。
停止后重新启用会复用原身份。

可在目标端 `iroh-remote enable`、客户端 `machine add` 或 `machine enable-iroh` 上重复传入 `--relay-url https://relay.example.net/`，GUI 中每行填写一个 relay URL。
机器配置对应 `kind: iroh`、`endpointId` 与 `relayUrls`；未指定时使用上游默认 relay。
双方需要可用的出站网络，无法直连时必须能访问所选 relay；公共 relay 没有本产品的可用性保证，带宽和服务限制以[上游说明](https://docs.iroh.computer/concepts/relays)为准。
设备发现与 relay 传输不是同一配置：自定义 relay 不会替换上游 N0 发现服务；不使用公共发现时，需要局域网 mDNS 实际可用，其可用性受平台权限和网络条件限制。
首次启用会等待可用的地址公告并实际验证 SSH 隧道，成功后才报告启用完成。

| 发行平台 | 从已有 SSH 自动启用目标 | 目标端后台运行机制 |
|---|---|---|
| Linux x64 | 支持 | 优先 systemd 用户服务；无用户管理器时为 detached 进程，重启后需重新启用 |
| macOS arm64 | 支持 | 有图形登录会话时为登录启动的 LaunchAgent；否则为 detached 进程 |
| Windows x64 | 不扩大现有 Windows 远端自动部署范围；在目标本地启用 | 当前用户登录启动的计划任务，不静默提权 |

Linux systemd 用户服务跨退出登录或开机运行需要管理员按系统策略启用该用户的 linger，命令输出会说明当前机制。
macOS 15 及以后，LaunchAgent 的局域网发现和直连需要用户在「系统设置 → 隐私与安全性 → 本地网络」允许 `dsharp Iroh`（`iroh-ssh`）；用途声明已嵌入接收端，但不会代替用户授权。
从 Terminal 或 SSH 启动的命令行及其子进程由 macOS 自动允许本地网络访问；从这两种上下文启用且没有图形登录会话时，detached 接收端沿用命令行上下文，无需为启用命令分配 TTY，并在启动 SSH 连接退出后继续运行。
这些规则不影响已有的 SSH 认证与 host-key 校验；未授权 LAN 且配置的 relay 不可达时，启用会失败并保留原 Endpoint ID，不会提权、替换 relay 或退回旧 IP。
详见 Apple [TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)。

可用发行平台以实际提供的 RID 资产为准；原生命令由用户独立安装，许可与补丁随显式准备的测试依赖保留，不进入普通产品归档。
跨架构编译原生测试依赖还需要相应编译器、sysroot 与目标工具链。

### 可选：由用户管理 systemd 服务

Host 按需启动时是独立后台进程，关闭 GUI 或 SSH 桥接不会结束其会话；产品不为 Host 安装 systemd 服务或修改 linger。
需要开机启动或由服务管理器监管时，可自行保存以下内容到 `~/.config/systemd/user/dsharp-host.service`，把 `ExecStart` 改为实际二进制的绝对路径：

```ini
[Unit]
Description=DeepSeek Harness Host

[Service]
Type=simple
ExecStart=%h/.dsharp/linux-x64/dsharp host serve
WorkingDirectory=%h
Restart=on-failure
KillMode=process

[Install]
WantedBy=default.target
```

```bash
mkdir -p ~/.config/systemd/user
# 将上面的 unit 保存为 ~/.config/systemd/user/dsharp-host.service 后执行：
systemctl --user daemon-reload
systemctl --user enable --now dsharp-host.service
# 查看或停止：
systemctl --user status dsharp-host.service
systemctl --user disable --now dsharp-host.service
```

服务与客户端须使用同一 harness home；默认是 `~/.dsh`，自定义根时在 unit 的 `[Service]` 中加入 `Environment=DSH_HOME=/absolute/harness/home`。
若同一 home 已有按需启动的 Host，请先结束该 Host 进程再启动服务；独占锁会阻止另一个进程接管仍在运行的 Host。
`KillMode=process` 使停止 Host 服务不会同时终止其 PTY daemon，后续 Host 可重接原终端。
需要退出登录后保留用户服务或开机即运行时，由用户按机器策略执行 `loginctl enable-linger "$USER"`（可能需要管理员授权）；应用不会代为执行。
外部服务的停止、升级与重启也由用户负责，自动部署不管理服务。

## 发布形态

- 默认 Release:JIT + 裁剪 + 自包含。
  ```bash
  dotnet publish DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj -c Release -r linux-x64
  ```
  支持运行时插件装载:`<安装目录>/plugins/` 启动加载,以及 `/plugins add <dll>` 热装载(可回收 AssemblyLoadContext,移除后校验回收)。
- NativeAOT(可选档):
  ```bash
  dotnet publish DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj -c Release -r linux-x64 -p:PublishAot=true
  ```
  - 启动更快、体积更小,覆盖镜像内的全部插件与 LLM 适配器;
  - 原生插件可在启动时从 `<安装目录>/plugins/` 加载并注册工具(ABI v1:日志与工具;插件需自带 NativeAOT 编译的共享库);
  - NativeAOT 不支持在运行期装载托管 dll:`plugins/` 中的托管插件启动时逐文件 WARN 并跳过(被跳过的插件不会出现在 `/plugins list`),其余插件照常启用、程序正常启动;要使用托管插件就把它编译进镜像;
  - 会话持久化(JSONL + Zstd)在 JIT 与 AOT 下均可用。


### Web 入口与独立宿主

TUI、GUI、Web 是同一产品的三个入口，共用 Host/Fleet 和会话数据；各自的宿主与发布目录用于隔离运行时依赖。
发布项目分别是 `DeepSeek-Harness-Sharp`（`dsharp`）、`Dsh.GuiHost`（`dsh-gui`）和 `Dsh.WebHost`（`dsharp-web`）。只有最后一个引入 `Microsoft.AspNetCore.App`：ASP.NET Core 的 HTTP/WebSocket、Cookie 认证和嵌入资源服务不会沿引用图进入 TUI/GUI。Web 的 Host/Origin 校验继续防止 DNS rebinding 与 CSRF，Cookie 为 HttpOnly/SameSite=Strict，令牌及 Cookie 密钥随进程重启失效。
登录页面及同源协议资源在登录前即可加载，但不包含访问令牌、机器清单或会话数据；后者仍需登录后才能读取。更新 WebHost 后重新加载页面即可使用随服务生成的同版本协议定义。

```bash
# 源码运行：WebHost 编译、激活 WebBridge 插件，再启动监听
dotnet run --project Dsh.WebHost/Dsh.WebHost.csproj -- --port 8780
# 独立发布（默认 JIT + partial trim + 自包含）
dotnet publish Dsh.GuiHost/Dsh.GuiHost.csproj -c Release -r linux-x64
dotnet publish Dsh.WebHost/Dsh.WebHost.csproj -c Release -r linux-x64
# 不裁剪的 JIT / NativeAOT 档使用同一个入口
dotnet publish Dsh.WebHost/Dsh.WebHost.csproj -c Release -r linux-x64 -p:PublishTrimmed=false
dotnet publish Dsh.WebHost/Dsh.WebHost.csproj -c Release -r linux-x64 -p:PublishAot=true
Dsh.WebHost/bin/Release/net10.0/linux-x64/publish/dsharp-web --port 8780
```

Release 将 Web 单独打包为 `dsharp-web-<rid>.tar.gz` / `dsharp-web-win-x64.zip`；AOT 资产在扩展名前加 `-aot`。默认安装脚本只安装原有 TUI/GUI 资产；需要 Web 时另下载对应平台和版本的 Web 资产，解压到独立目录，不要把 ASP.NET 程序集覆盖到 TUI/GUI 发布目录。把 Web 目录加入 PATH（或将 `dsharp-web` 链接到命令目录）即可保留 `dsharp web` 用户入口，例如：

```bash
mkdir -p "$HOME/.local/share/dsharp-web" "$HOME/.local/bin"
tar -xzf dsharp-web-linux-x64.tar.gz -C "$HOME/.local/share/dsharp-web"
ln -sf "$HOME/.local/share/dsharp-web/dsharp-web" "$HOME/.local/bin/dsharp-web"
dsharp-web --print-token    # 另一个终端已经运行 dsharp-web 时，打印该 home 的登录链接
```

Windows 可将 `dsharp-web-win-x64.zip` 解压到 `%LOCALAPPDATA%\\dsharp-web` 并将该目录加入用户 PATH，直接运行其中的 `dsharp-web.exe`。WebHost 与其它入口使用相同的 `DSH_HOME` / `settings.storage.root`；WebHost 自身也能执行共享的 `host serve` 命令，本机 Host 启动不依赖猜测另一个构建目录。

发布验证检查同一轮构建产生的三个入口，核对非 Web 的 `.deps.json`、`runtimeconfig.json` 和程序集隔离，打印各目录总字节数，并启动发布版进行 HTTP 登录、跨站拒绝、CSP、嵌入资源和 WebSocket/Fleet 转发冒烟；同一检查也用于 Web 的 NativeAOT 产物与解压后的 Release 归档：

```powershell
./scripts/smoke-web-host.ps1 `
  -WebPublishDir Dsh.WebHost/bin/Release/net10.0/linux-x64/publish `
  -TuiPublishDir DeepSeek-Harness-Sharp/bin/Release/net10.0/linux-x64/publish `
  -GuiPublishDir Dsh.GuiHost/bin/Release/net10.0/linux-x64/publish
```
## 各版本新增功能

0.1.1 起的发布说明未单独张贴,这里按版本折叠列出新增能力(0.1.0 的完整发布说明见[讨论区](https://github.com/deepseek-ai/deepseek-harness/discussions/8952))。

<details>
<summary><b>未发布</b> — SSH-over-iroh 与 SSH 命令流代理</summary>

- 多机连接新增 Endpoint ID 接入、独立可选 `Dsh.Fleet.Client.Iroh` 程序集、已有 SSH 自动启用与目标端本地启用两种入口，沿用原有 SSH 认证和 Fleet 会话生命周期。
- 原生 `iroh-ssh` 由用户安装并通过启动进程的 `PATH` 检测，产品构建与发行包不下载、不捆绑；缺少命令时不显示 GUI 新增入口，不改动已有配置。
- 接收端要求兼容的 `server --ready-file` 命令，同步地址发现就绪再验证 SSH 隧道；仓库保留显式原生测试依赖准备脚本、固定来源和补丁说明。
- macOS 接收端嵌入本地网络用途声明，局域网接入遵循用户授权，不静默提权。
- 使用方法与平台差异见「通过 iroh 接入没有公网 SSH 地址的机器」。
- CLI 新增 `machine tunnel` 配置、实际 HTTP(S) 自检及共享出口的 `use`/`direct`，TUI 通过 `/proxy via`、`/proxy direct` 与 `/proxy reload` 使用同一选择；受管租约保留认证、TLS 和连接释放边界，不修改基准或远端 Host 的出口配置。
- `doctor --proxy-layer` 分别检查 SSH、本机基准出口和目标 Host 出口，输出带阶段、HTTP 状态与操作身份的结构化结果；独立取消会确认目标探测资源释放，不影响其他消费者。

</details>


<details>
<summary><b>0.1.1</b> — SSH 远程工作区、macOS 支持、TUI 增强</summary>

- **SSH 远程工作区**:新增中立传输层(长度分帧 + JSON-RPC 2.0)与远端宿主,GUI 可保存/打开远端工作区,自动在远端部署 `dsharp` 并测试连接,远端会话/消息/工具/文件/终端(PTY)/审批走同一条通道。
- **命令改名**:`dsh` → `dsharp`。
- **macOS**:加入 macOS 的 CI 测试与 JIT 发布,修复 Unix PTY 绑定与 GUI 主线程启动。
- **TUI**:24 位/256 色真彩渲染;attach 或调整尺寸后整帧重绘;Pty→Session→Pane 总览;`Ctrl+X ,` / `Ctrl+X .` 切换同一 Pty 下的会话;`/plugins remove` 变为真正卸载。
- **时间线/回滚**:`/timestamp` 预览与跳转、会话截断、检查点回滚。
- **其它**:提示词按路径引用的图片可随请求发送;过大的工具结果自动落盘;原生 AOT 插件加载带故障遏制;看板新增 `board_claim`/`board_release` 软行锁。
</details>

<details>
<summary><b>0.1.2</b> — 远程工作区打磨</summary>

- 远端会话跟随客户端选择的模型与提供方。
- 远端部署按发布版本取键,过期守护进程自动重启。
- 修复 Avalonia 12.1.3+ 上托盘图标不显示;Avalonia 升到 12.1.4。
- GUI 远端会话列表放入独立滚动区。
</details>

<details>
<summary><b>0.3.0</b> — 远程宿主拆分、Iroh 传输与 GUI 工作区</summary>

- **远程宿主**:拆分 Host 客户端/服务端契约, SSH 与 Iroh 作为可选传输;远端自动部署按协议版本标记,旧协议自动重新部署。
- **Iroh**:新增显式 Iroh 传输与机器配置;目标端接收器命令改名为 `dsharp iroh-remote enable|info|disable`,仅新增 Iroh 机器时显示设置说明。
- **网络**:SSH local-forward、proxy-command 与 borrowed route;分层代理探测和可取消的超时;设置页与 CLI 共用网络配置。
- **GUI**:远端会话进入共享聊天/终端界面;会话列表可直接继续、重命名、删除;新增 Git 页面(仓库选择、提交 diff);机器编辑、目录选择与探测结果整理;自由输入问答选项。
- **安全**:提示词引用的文本文件不再自动解析图片路径;图片改用 `read_image` 工具读取,避免大量图片拖垮上下文。
- **设置**:`settings.yaml` 模板成为唯一来源,删除内置精简模板回退。
</details>

<details>
<summary><b>0.2.0</b> — Vulkan 渲染后端、可配置主题、设置面统一</summary>

- **Vulkan 渲染后端**:TUI 新增可插拔 GPU 宿主抽象与 Vulkan 后端,按 DRM 卡的 PCI 槽位选设备;裸控制台可经 SDL KMS/DRM 直接驱动;无 root 即可安装 Vulkan loader;OpenGL 后端拆分到独立模块。
- **界面**:TUI 颜色可配置;会话选择显示标题与时间戳;IDE 历史并入 `/timestamp` 时间线(可开关);同一守护进程下的 Pty 可直接进入。
- **设置**:插件配置面在编译期生成 schema;GUI 列出提供方生效后的模型;`settings.yaml` 只保留 models.dev 未收录的模型;存储路径统一到一个根。
- **会话**:`fork` 改为按事件种子继承,修复派生会话内容缺失。
- 发布 macOS JIT 构建。
</details>

## settings.yaml 结构

配置文件为 `~/.dsh/settings.yaml`;首次运行按 `src/Dsh.Boot/Profiles/Templates/settings.yaml` 使用带注释的模板作为默认配置,文件在首次保存设置时创建,不覆盖已有配置(可用 `$DSH_HOME` 或 `storage.root` 指定其它 harness home 做隔离测试)。
`/plugins` 命令写回 `plugins:` 段时采用文本级修改(保留注释)。

```yaml
# 全局默认模型 / 压缩模型 / 子代理默认模型,格式均为 provider/model
global_default_model: deepseek-official/deepseek-v4-flash
compaction_model: deepseek-official/deepseek-v4-flash
subagent:
  default_model: deepseek-official/deepseek-v4-flash

# LLM 提供方:options 为连接参数,models 为该提供方可用模型
# type 缺省即 openai-compatible;DeepSeek 专属行为(缓存 token 统计等)需显式 type: deepseek
# 适配器由插件提供(dsh-llm-openai / dsh-llm-anthropic / dsh-llm-deepseek),可禁用或换用第三方插件;
# 未配置 key 的 provider 会被跳过并记 WARN;完全不配 provider 也能启动,首次发起请求时才报错
providers:
  deepseek-official:
    type: openai-compatible
    options:
      baseUrl: https://api.deepseek.com
      apiKeyEnv: DEEPSEEK_API_KEY
    models:
      deepseek-v4-flash: { name: DeepSeek V4 Flash, reasoning: true, tool_call: true }
      deepseek-v4-pro:   { name: DeepSeek V4 Pro,   reasoning: true, tool_call: true }

skills:                 # 技能目录与 URL
  paths: []
  urls: []
rules: []               # 全局规则
sandbox:                # 插件沙箱开关,默认 false(关闭时插件装载与运行方式不变)
  enabled: false
plugins: {}             # 插件开关,见下
mcp: {}                 # MCP 服务器:transport: stdio|sse|streamable-http,配 command/args/url/enabled
memory:                 # 项目记忆(/memory on|off;file 或 mongo 后端)
  enabled: false
  # backend: mongo      # 可选:mongo 时用数据库存整段 markdown
  # mongo: { connectionString: mongodb://localhost:27017, database: dsh_memory, collection: memory, key: project }
compaction:             # 自动压缩
  auto: true
  prune: true
checkpoints:            # shadow git 恢复点
  enabled: false
  max_points: 256
  keep_days: 15
logging:                # 日志:trace|debug|info|warn|error|off
  level: info
  buffer_size: 1000
  console: false
  file: true
  file_max_mb: 32
  keep_days: 15
safety:
  autoApprove: false
  blacklist: []
```

本机出口由 GUI 设置「网络」与 TUI 共用:

```yaml
network:
  outbound_proxy:
    url: socks5://user:pass@127.0.0.1:1080
    bypass: [api.deepseek.com]
    probe_url: https://www.gstatic.com/generate_204
```

`url` 支持 HTTP、HTTPS、SOCKS4/4a/5，留空跟随系统代理；日志与自检脱敏代理凭据。
GUI「保存本机基准出口」只更新本机基准并应用到后续请求，不会解除借用选择；TUI `/proxy set <URL>` 写回并热更新，`/proxy clear` 恢复系统代理（保留直连名单与自检地址），手改 settings.yaml 后用 `/proxy reload` 热更新。
`/proxy test [URL]` 与 GUI「检查本机出口代理」均检查本机基准，区分代理连接、认证与目标访问失败，不代表借用出口已经验证。

GUI「设置 → 远程」把 SSH 连接代理与目标 Host 的一般网络出口分开。
SSH 检查可以使用尚未保存的机器表单，只验证连接，不部署或保存。
目标 Host 出口先读取并核对页面显示的 OwnerId、HostHome 与 Revision，再编辑、保存或清除；它由该 Host 的所有 workspace 和连接别名共享，不是本机借用隧道设置。
读取/刷新会替换当前远端草稿，检查只使用当前草稿而不写盘；发生冲突或重连后须重新读取，保留草稿不会自动重放保存。
远程机器列表和详情独立滚动，较窄窗口自动上下排列；不支持相关协议的旧 Host 会明确显示不支持，不改写本机出口。
本机发布包下载仍走本机有效出口，远端下载部署包的 `fleet.deploy.serverProxy` 也不会替代目标 Host 的一般网络出口。

有效的出口地址、代理认证或直连名单变化会取消旧路由代次的在途请求,关闭旧连接池并释放旧凭据;新请求使用新代配置,同一路由的请求仍复用连接。
系统不会自动重放被取消的请求;非法或等效的配置不会中断当前请求。

`web_fetch` 不因配置代理而放宽公网限制:每次请求及重定向均在本机解析并拒绝非公网或混合公网/私网答案;直连钉住已校验地址,经代理则以已校验的 IP 作为 HTTP 绝对 URI、CONNECT 或 SOCKS 的实际目标,同时保留原始 Host、TLS SNI 与正常证书域名校验。代理不再替 `web_fetch` 解析目标域名,因此本机 DNS 不可用时请求明确失败。代理必须支持 IP 目标及原始 Host;拒绝这种路由的代理会报真实访问失败,不会回退到未校验的域名。代理服务器本身是可信传输边界,恶意代理仍可无视所请求的地址(HTTP 无端到端加密,HTTPS 仍校验证书);本机不声称能控制远端 DNS。其他 HTTP 客户端沿用各自的目标策略。

- `plugins:` 支持两种写法:`"@deepseek-ai/dsh-tool-todo": false`(禁用),或带参数的 `"@deepseek-ai/dsh-tool-todo": { enabled: true, ... }`(保存插件参数);插件被发现即启用,这里的禁用项与 `/plugins` 命令是仅有的两个开关来源。
- 插件权限:`permissions` 是与 `enabled` 并列的保留键(不会作为参数交给插件),含 `read`/`write`/`network` 三项布尔值;未写 `permissions` 或未写某一项时均为允许。由 `/plugins ban_write|enable_write` 与 GUI 插件市场的勾选框写入,也可手写;拼错的键或非布尔值会在启动时报 `INVALID_PLUGIN_SETTING`。目前只记录配置,强制执行由后续版本的插件沙箱(`sandbox.enabled: true`)提供:

  ```yaml
  sandbox:
    enabled: false
  plugins:
    "@acme/foo":
      enabled: true
      permissions: { read: true, write: false, network: true }
  ```
- 运行期数据:`<home>/logs/dsh-YYYYMMDD.log`(按天 + `file_max_mb` 切分、`keep_days` 清理),`<home>/sessions/<工作目录转写>/<会话 id>/session.jsonl.zstd`,`<home>/telemetry.jsonl`(agent 生命周期与错误事件,可用 `plugins: {"@deepseek-ai/dsh-telemetry": false}` 关闭)。
- 子代理模型:模型可调用 `list_subagent_models` 查看子代理能用的模型路由,即已配置 key 的 provider 及其生效模型(`settings.yaml` 声明的模型 ∪ models.dev 目录),模型公布了推理强度时一并列出;`subagent` 工具的 `provider`/`model`/`reasoning_effort` 参数直接取用其中一行,不填则沿用该工具插件参数里的 `agentOptions`,其次是 `settings.yaml` 的 `subagent.default_model`,再缺省则跟随当前会话的模型。未配置 key 的 provider 不会出现在列表中。可用 `plugins: {"@deepseek-ai/dsh-tool-subagent/list-models": false}` 关闭该工具。
- 会话检索:TUI/CLI 内 `/session-query <关键词>` 搜索历史会话(命中用 `/session <id>` 打开);模型侧对应 `session_search` 工具。检索覆盖内存中的活会话与 `sessions/` 下的历史日志。系统 SQLite 提供 FTS5 时使用全文分词索引,否则使用本地文本匹配;无需为普通会话查询单独安装 SQLite,平台依赖与匹配差异见「安装」。
- 交付物:模型生成报告、Office 文档、表格、幻灯片等文件后,用 `present` 工具把已存在的文件声明为最终交付物(只记录绝对路径,不复制、不转换内容;路径不存在或是目录会报错)。清单随会话日志保存,恢复会话后仍在,回退/截断会话时一并回退。TUI 会话流里显示交付物卡片(绝对路径可直接复制),`/deliverables` 列出本会话交付物,`/deliverables open <编号>` 用系统默认程序打开;GUI 见「图形界面」一节。不需要时用 `plugins: {"@deepseek-ai/dsh-tool-present": false}` 关闭。
