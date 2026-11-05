---
name: rider-run-config
description: 在 JetBrains Rider 中为 .NET/C# 工程创建持久 run configuration 并配合调试器使用。
---

## 适用范围

- Rider + .NET/C# 工程。
- 需要在 IDE 中通过 run configuration 启动/调试 `Program.Main`、测试或可执行项目。
- 已有 `jetbrains-run-config` skill 主要面向 IntelliJ IDEA/Python；Rider 的加载机制不同，以本 skill 为准。

## 核心结论：Rider 从 workspace.xml 加载

Rider 的 .NET run configuration **不是**从 `.idea/runConfigurations/*.xml` 自动加载（本项目已实测：该目录下的文件不会被 `rider_get_run_configurations` 列出）。

实际加载位置在：

```
<project>/.idea/.idea.<SolutionName>/.idea/workspace.xml
```

其中 `<component name="RunManager">` 内的 `<configuration>` 节点才是 Rider 识别的 .NET 运行配置。

## 操作流程

### 1. 找到 workspace.xml

```text
.idea/.idea.<SolutionName>/.idea/workspace.xml
```

用 `rider_read_file` 读取 `RunManager` 节点，确认现有配置格式。

### 2. 添加 .NET Project 配置

在 `RunManager` 的现有 `<configuration>` 之间插入：

```xml
<configuration default="false" name="MyRunConfig" type="DotNetProject" factoryName=".NET Project">
  <option name="EXE_PATH" value="" />
  <option name="PROGRAM_PARAMETERS" value="--profile headless --home /path/to/home &quot;task&quot;" />
  <option name="WORKING_DIRECTORY" value="$PROJECT_DIR$" />
  <option name="PASS_PARENT_ENVS" value="1" />
  <option name="ENV_FILE_PATHS" value="" />
  <option name="REDIRECT_INPUT_PATH" value="" />
  <option name="MIXED_MODE_DEBUG" value="0" />
  <option name="USE_MONO" value="0" />
  <option name="RUNTIME_ARGUMENTS" value="" />
  <option name="AUTO_ATTACH_CHILDREN" value="0" />
  <option name="PROJECT_PATH" value="$PROJECT_DIR$/Path/To/YourProject.csproj" />
  <option name="PROJECT_EXE_PATH_TRACKING" value="1" />
  <option name="PROJECT_ARGUMENTS_TRACKING" value="1" />
  <option name="PROJECT_WORKING_DIRECTORY_TRACKING" value="1" />
  <option name="PROJECT_KIND" value="DotNetCore" />
  <option name="PROJECT_TFM" value="" />
  <method v="2">
    <option name="Build" />
  </method>
</configuration>
```

关键点：

- `type` 必须是 `DotNetProject`，不是 `DotNetProjectConfiguration`。
- `factoryName` 必须是 `.NET Project`。
- `PROJECT_PATH` 指向目标 `.csproj`。
- `PROGRAM_PARAMETERS` 中引号要写成 `&quot;`。
- `default="false"` 表示不是 IDE 临时配置。

### 3. 验证已被 Rider 加载

调用：

```
rider_get_run_configurations
```

如果返回列表中出现新配置名，说明加载成功。

若没有出现，继续检查：

- 是否写错了位置（必须位于 `RunManager` 组件内）。
- 是否是 `.idea/runConfigurations/` 下新建文件（Rider .NET 配置不会从那里加载）。
- 是否缺少 `type`/`factoryName` 大小写或拼写。

## 调试使用

1. 用 `rider_xdebug_set_breakpoint` 设置断点。
2. 用 `rider_xdebug_start_debugger_session`：

```
rider_xdebug_start_debugger_session(configurationName="MyRunConfig")
```

3. 用 `rider_xdebug_get_stack` / `rider_xdebug_get_frame_values` / `rider_xdebug_get_value_by_path` 检查暂停点。

## 注意

- 不要直接依赖 `.idea/runConfigurations/` 目录给 Rider .NET 工程添加配置。
- 修改 `workspace.xml` 后若 Rider 未立刻感知，可再次调用 `rider_get_run_configurations` 确认；必要时让 IDE 重载项目或重启 Rider。
- `rider_xdebug_*` 工具基于 IDE 已加载的 run configuration 工作，因此必须先让配置出现在 `rider_get_run_configurations` 中。