---
name: interactive-ssh
description: "通过 interactive.yml 在 GitHub 托管的 Windows、macOS 或 Linux runner 上开 upterm SSH 会话，直接在目标平台上构建、运行和调试测试。在本机无法复现平台专属失败、需要反复验证 Windows/macOS 行为，或想避免为每次尝试推送并等待完整 CI 时使用。"
---

# 在 CI runner 上开 SSH 会话调试

本机能复现的问题在本机调试，不为此占用 runner。
平台专属失败先用一次会话复现并定位，再改代码，不靠反复推送正式 CI 试错。
本技能不授予额外的公开写入、权限提升或范围扩张授权。

## 前提

- 调试用仓库的默认分支上必须有支持 upterm 的 `.github/workflows/interactive.yml`；手动触发只读取默认分支上的 workflow 文件。
- 要检出的分支或 SHA 必须已推送到该仓库；workflow 从默认分支运行，按 `ref` 参数检出目标代码，目标分支本身不需要带这个 workflow。
- 连接用私钥的公钥必须在触发者 GitHub 账号上，或在仓库 Actions variable `INTERACTIVE_SSH_AUTHORIZED_KEYS` 中（每行一个公钥）；两处都没有时 workflow 直接失败，不会开放会话。
- agent 应使用专用密钥并登记到仓库 variable，而不是把密钥加到用户 GitHub 账号；账号级 SSH key 能推送该账号的所有仓库。
- 生成密钥时显式设置不含用户名、主机名的注释，例如 `ssh-keygen -t ed25519 -N "" -C "dsh-ssh-agent" -f <路径>`。
- `gh` 已登录且对该仓库有 Actions 写权限。

## 开会话

```bash
bash .agents/skills/interactive-ssh/scripts/open-session.sh \
  --repo <owner>/<repo> --key <私钥路径> --os windows-latest --ref <分支或SHA> --timeout 60
```

- `--os` 可选 `windows-latest`、`ubuntu-latest`、`macos-latest` 或 `all`，只开需要的平台。
- `--timeout` 是作业总时长上限（分钟），GitHub 托管 runner 最多 360，到时会话随作业结束。
- 脚本触发 workflow、等待各 runner 上传连接信息 artifact，并生成 ssh 配置；输出里给出每个平台的 `ssh -F <配置> <平台>` 和工作目录。
- 从触发到可连接通常约 1 分钟。
- job 运行期间 GitHub API 读不到日志，annotation 也要等 job 结束才出现，所以连接信息只通过 artifact `ssh-<平台>` 交出。
- 手动触发的 run 在列表里显示的分支是默认分支，被测 ref 看 job 名和 artifact 内容。

## 执行命令

- 用 `ssh -F <配置> <平台> '<命令>'` 非交互执行，用 `scp -F <配置>` 传文件；会话以 `--full-access` 启动，无需逐次批准。
- Windows 上显式命令由 PowerShell 执行，需要 bash 时包成 `"bash -lc '<命令>'"`，路径用 `/d/a/...` 形式。
- 不带命令的 `ssh -F <配置> <平台>` 加入共享终端，适合人工观察，agent 不要依赖它。
- 工作目录写在 `<输出目录>/<平台>/workspace.txt`，命令先 `cd` 到这里。
- 长时间命令保持单个 SSH 连接（配置已设 `ServerAliveInterval`），输出重定向到 runner 上的日志文件，再分段读取，不在一个命令里打印海量输出。

## 构建与测试

- runner 已检出目标 ref，并准备好 `packages/` 下的 DryIoc feed。
- 构建测试项目：`dotnet build tests/Dsh.Tests/Dsh.Tests.csproj -c Debug --nologo -p:DshNativeCompiler=<cl|clang>`；Windows 用 `cl`，Linux/macOS 用 `clang`，与正式 CI 一致。
- 运行测试：`tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests[.exe] -noColor -class <类> -method <方法>`，并按 AGENTS.md 加上排除 GPU、渲染压测和 LLM 联网的 trait 过滤。
- 需要正式 CI 的额外前置条件时（真实 C# 前端、PTY daemon、Iroh 夹具），照 `.github/workflows/ci-tests.yml` 对应步骤在会话里补齐，不猜测环境。

## 同步代码

- 在本机提交并推送到同一仓库的分支后，在会话里 `git fetch origin <分支> && git checkout --detach FETCH_HEAD`，再增量构建。
- 只改一两个文件做试验时可以用 `scp` 覆盖 runner 上的文件，但确认的修复必须回到本机提交，runner 上的改动会随会话丢失。
- 在会话里确认修复后，再推送正式 PR 并等待正式 CI，不把会话结果当成正式 CI 通过。

## 结束会话

- 用完立即结束：`ssh -F <配置> <平台> "bash -lc 'touch <工作目录>/continue'"`，runner 发现该文件后结束作业。
- 也可以 `gh run cancel <run> -R <owner>/<repo>`。
- 不要让 runner 空等到超时。

## 隐私与安全

- 连接命令里含会话令牌，不得写进 Issue、PR、Discussion、提交或任何公开文本；本地生成的配置和下载的 artifact 放在临时目录，用完即删。
- 不在会话里打印仓库 secret、令牌或私钥。
- 授权列表以外的密钥会被拒绝；不要为方便而去掉授权列表或让会话变为开放状态。
