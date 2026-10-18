## 变更

Closes #70

- 将原 Dsh.Tools 拆为 Dsh.Tools.Fs、Dsh.Tools.Search、Dsh.Tools.Shell、Dsh.Tools.Plan；持久 bash/pwsh 从 Jobs 迁入 Shell。
- 保留工具实现、模型 schema/描述/输出、服务生命周期和逐插件启停；四个能力工程不互相引用。
- 迁移全部 14 项插件 ID、宿主装配/发布图、配置模板、用户迁移说明和调用方测试。
- 对任何旧 ID（包括 false/null/旧新混用）给出显式迁移错误，不保留旧注册或兼容别名；非法 plugins 根段和 enabled 值明确拒绝。

ID 后缀命名是本 PR 提交给作者审阅的提案，不代表作者已批准。
原本未消费的一次性 shell/Search 参数不因拆包变得生效，schema 生成器仍按程序集首个 ID 汇总标注类型，README 已说明运行时绑定边界。

## 验证

- .NET SDK 10.0.402，Linux x64；本地 DryIoc feed 按仓库脚本填充。
- 全解决方案构建成功，0 warning、0 error。
- 相关回归：201 项，176 通过、0 失败、25 跳过。
- 完整 quick：2704 项，2668 通过、0 失败、32 跳过、4 pending；仅使用仓库规定的 GpuStress/OnlyGpu/RenderBench/LlmSmoke 四项排除。
- 实际改前/改后默认完整 49 项目录，以及直接组合真实能力包的完整 14 项目录，全文件逐字一致，包含名称、描述、参数 schema、输出 schema 和顺序。
- quick 之后驱动真实构建 CLI 的默认配置、四域禁用、旧新混用、非法开关/根段、模板验证和 config schema；真实 ConfigBoot 参数载入也完成。

精确命令、计数、二进制实际退出状态、SHA-256 和平台限制见：

- `plans/工具拆包实施记录.md`
- `plans/工具拆包交付证据.json`
- `plans/工具拆包快速回归.json`
- `plans/工具拆包CLI默认.log`、`plans/工具拆包CLI禁用.log`、`plans/工具拆包CLI旧新混用.log`
- `plans/工具拆包完整目录最终.json`、`plans/工具拆包能力目录最终.json`

## 已知边界

Windows/macOS 和未安装的 pwsh 未执行；NativeAOT、真实 SSH、MongoDB/systemd、两个需已配置 HOME 的 TUI/PTy 场景跳过，四个 Explicit outbound-proxy/LLM smoke 未运行。
五个既有 Pwsh 测试在 shell 缺失时提前返回，不将 runner pass 当作 PowerShell 覆盖。
原始提交重复捕获已复现完整默认宿主跨域激活顺序不恒定；本次不修改调度器，确定性回归严格比较真实能力包的全部 14 项目录。
最终一次完整默认 49 项捕获与原始基线逐字一致，不声称任意未来启动全局顺序恒定。

Rider MCP 和 csharp-ls 在本环境不可用，按任务授权使用编译器/analyzers、仓库测试和真实产品入口，没有声称运行过 Rider。
私有 Xvfb、QA 子进程/socket、专属临时检出和缓存均已清理，保留主工作树供复核。
秘密扫描仅报既有无凭据 localhost MongoDB 默认地址；原始命中和人工核实记录随证据保留。
此 PR 未合并，CI 状态以 GitHub 实际检查为准。
