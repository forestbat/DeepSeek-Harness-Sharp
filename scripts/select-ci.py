"""Select CI consumers from changed paths without requiring third-party packages.

规则按"改动会影响什么"给出最小够用的套件, 而不是把所有路径都映射到全部套件:

- `.github/workflows/**`、本脚本、`tests/ci/**` 只影响 CI 自身, 跑 actionlint 与选测规则单测即可;
- `src/Dsh.Android/**` 只影响 APK, 不牵动桌面三入口;
- 原生源码、构建元数据影响所有入口与发布产物, 一律全跑;
- `tests/Dsh.Tests/**` 影响 quick 与 regression(regression 的类全部包含在 quick 里, 它只是独立的第二份运行);
- 其余 `src/**`、`scripts/**`、未知路径走 quick + 发布校验。
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


# regression 的类都在 quick 的范围内: 两套件跑的是同一批用例, quick 只是把它们分片并行。
SUITES = ("quick", "regression", "raw")

DOCUMENTATION_PREFIXES = ("docs/", "plans/", ".omo/", ".github/ISSUE_TEMPLATE/")

# 构建元数据: 改这些等于改所有入口的产物与发布档。
BUILD_METADATA_NAMES = {
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "NuGet.Config",
}
BUILD_METADATA_SUFFIXES = (".csproj", ".props", ".targets", ".slnx", ".sln")

# 终端原始输入与 PTY 语义由 raw 套件在真实 TTY 上验证。
RAW_MODE_PREFIXES = ("src/Dsh.Tui/", "src/Dsh.Pty/", "src/Dsh.PtyTerminal/")


def is_documentation(path):
    if path.startswith(DOCUMENTATION_PREFIXES):
        return True
    if path == ".github/PULL_REQUEST_TEMPLATE.md":
        return True
    return "/" not in path and (
        path.endswith(".md") or path == "LICENSE" or path.startswith("LICENSE.")
    )


def is_build_metadata(path):
    name = os.path.basename(path)
    return name in BUILD_METADATA_NAMES or path.endswith(BUILD_METADATA_SUFFIXES)


def classify_changes(paths, event, aot=False, full=False):
    """Return consumer selections; paths may name files that no longer exist."""
    full = full or event == "workflow_dispatch"
    suites = set(SUITES) if full else set()
    jit = full
    validate = full
    android = full
    for path in paths:
        if is_build_metadata(path):
            suites.update(SUITES)
            jit = True
            android = True
            continue
        if is_documentation(path):
            continue
        if path.startswith(".github/workflows/") and path.endswith((".yml", ".yaml")):
            validate = True
            continue
        if path == "scripts/select-ci.py" or path.startswith("tests/ci/"):
            validate = True
            continue
        if path.startswith("src/Dsh.Android/"):
            android = True
            continue
        if path.startswith("native/"):
            suites.update(SUITES)
            jit = True
            continue
        if path.startswith("tests/Dsh.Tests/"):
            suites.update(("quick", "regression"))
            # 终端原始输入的端到端回归由 raw 套件在真实 TTY 上跑。
            if "RawMode" in path:
                suites.add("raw")
            continue
        if path.startswith(RAW_MODE_PREFIXES):
            suites.update(SUITES)
            jit = True
            continue
        # 其余源码、脚本与未知路径: 覆盖最快的一道测试加发布校验。
        suites.add("quick")
        jit = True
    return {
        "tests": bool(suites),
        "jit": jit,
        # The APK bundles the same shared assemblies the desktop entries publish.
        "android": android or jit,
        "aot": jit and (aot if event == "workflow_dispatch" else event == "pull_request"),
        "validate": validate,
        "suites": [suite for suite in SUITES if suite in suites],
    }


def changed_paths(base, head):
    result = subprocess.run(
        ["git", "diff", "--no-renames", "--name-only", "-z", base, head, "--"],
        stdout=subprocess.PIPE,
        check=True,
    )
    return [os.fsdecode(path) for path in result.stdout.split(b"\0") if path]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", required=True, choices=("push", "pull_request", "workflow_dispatch"))
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--aot", required=True, choices=("true", "false"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    full = args.event == "workflow_dispatch" or (
        args.event == "push" and (not args.base or set(args.base) == {"0"})
    )
    try:
        paths = [] if full else changed_paths(args.base, args.head)
    except (OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Unable to determine CI changes: {error}\n")
    selection = classify_changes(paths, args.event, args.aot == "true", full=full)
    lines = []
    for key, value in selection.items():
        encoded = json.dumps(value, separators=(",", ":"))
        lines.append(f"{key}={encoded}\n")
    if args.output is None:
        sys.stdout.write("".join(lines))
    else:
        with args.output.open("a", encoding="utf-8") as output:
            output.writelines(lines)


if __name__ == "__main__":
    main()
