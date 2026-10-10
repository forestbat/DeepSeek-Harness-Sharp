#!/usr/bin/env bash
#
# 判定一个发布输出目录是不是真的 NativeAOT 产物。
#
# 背景: 只检查"可执行文件存在"是不够的 —— JIT 发布同样会产出 apphost（约 0.15MB）。
# 一旦 PublishAot 静默失效（例如 restore 与 publish 的属性不一致、缺少 ILCompiler 包），
# 就会把一份 JIT 产物当成 AOT 发出去。本脚本用**结构特征**判别，不用体积阈值：
#   AOT 镜像不含托管主程序集、运行时配置与 CoreCLR 运行时文件。
#
# 用法: assert-aot-output.sh <publish-dir>
set -euo pipefail

DIR="${1:?用法: assert-aot-output.sh <publish-dir>}"
if [ ! -d "$DIR" ]; then
    echo "::error::目录不存在: $DIR" >&2
    exit 1
fi

APP="dsharp"

if [ ! -f "$DIR/$APP" ] && [ ! -f "$DIR/$APP.exe" ]; then
    echo "::error::AOT 输出里没有可执行文件 $APP（或 $APP.exe）: $DIR" >&2
    ls -la "$DIR" | head -20 >&2
    exit 1
fi

# JIT 发布的标志物。这些文件在 AOT 镜像里都不该出现。
# 覆盖 win/linux/macOS：CoreCLR 与 hostfxr 在 Windows 是 .dll、Linux 是 .so、macOS 是 .dylib。
JIT_MARKERS=(
    "$APP.dll"
    "$APP.runtimeconfig.json"
    "$APP.deps.json"
    "System.Private.CoreLib.dll"
    "coreclr.dll"
    "libcoreclr.so"
    "libcoreclr.dylib"
    "hostfxr.dll"
    "libhostfxr.so"
    "libhostfxr.dylib"
)

FOUND=()
for m in "${JIT_MARKERS[@]}"; do
    if [ -e "$DIR/$m" ]; then
        FOUND+=("$m")
    fi
done

if [ "${#FOUND[@]}" -ne 0 ]; then
    echo "::error::输出带 JIT 发布标志物（${FOUND[*]}）→ 这不是 AOT 构建，PublishAot 没生效: $DIR" >&2
    ls -la "$DIR" | head -20 >&2
    exit 1
fi

echo "AOT 输出校验通过: $DIR（原生可执行文件，无托管主程序集/运行时配置/CoreCLR）"
