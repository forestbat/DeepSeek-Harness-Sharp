#!/usr/bin/env bash
#
# 确保本地 NuGet feed(packages/)包含 Dsh.Runtime 依赖的 DryIoc.dll。
# 需要的版本直接从 src/Dsh.Runtime/Dsh.Runtime.csproj 的 PackageReference 读取。
# 构建用的 commit 取 dadhi/DryIoc 默认分支的最新 commit(运行时解析, 不硬编码)。
# 该版本不在 nuget.org 的 DryIoc 线上, 且包 ID 是 DryIoc.dll 而非 DryIoc。
# 默认从源码构建; 本地已有 nupkg 时比较其构建 commit 与远程最新 commit, 不一致(或无标记)则重建。
# 每个 nupkg 旁边的 <包名>.commit 戳记文件记录它构建自哪个 commit。
#
# 用法:
#   ./scripts/fetch-dryioc-feed.sh
#   ./scripts/fetch-dryioc-feed.sh --force                       # 不管戳记是否一致都重建
#   ./scripts/fetch-dryioc-feed.sh --proxy http://<host>:<port>   # 访问 GitHub 走指定代理, 不传则直连
#   ./scripts/fetch-dryioc-feed.sh --use-gh-artifact   # 本机有已登录的 gh CLI 时, 优先直接从 CI 产物下载
#
# 依赖: git 与 dotnet SDK。
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
FEED_DIR="$ROOT_DIR/packages"
WORK_DIR="$ROOT_DIR/artifacts/.dryioc-src"
CSPROJ="$ROOT_DIR/src/Dsh.Runtime/Dsh.Runtime.csproj"

PACKAGE_ID="DryIoc.dll"
REPO_URL="https://github.com/dadhi/DryIoc.git"
TARGET_FRAMEWORK="net9.0"

GH_REPO="dadhi/DryIoc"
GH_WORKFLOW="ci.yml"
GH_ARTIFACT="packages"

USE_GH_ARTIFACT=0
PROXY=""
FORCE=0

usage() {
    cat <<'EOF'
用法: fetch-dryioc-feed.sh [选项]

选项:
  --proxy <url>       访问 GitHub 走指定代理(如 http://<host>:<port>), 不传则直连
  --use-gh-artifact   若本机有已登录的 gh CLI, 优先从 CI 产物下载, 失败则回退到源码构建
  --force             不管 commit 戳记是否一致都重建
  -h, --help          显示本帮助
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --proxy) shift; PROXY="${1:?--proxy 需要一个值}" ;;
        --proxy=*) PROXY="${1#--proxy=}" ;;
        --use-gh-artifact) USE_GH_ARTIFACT=1 ;;
        --force) FORCE=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "未知参数: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# 指定代理时对本脚本启动的子进程生效(git/gh/dotnet 都识别这些标准变量); 未指定则直连。
if [ -n "$PROXY" ]; then
    case "$PROXY" in
        *://*) ;;
        *) PROXY="http://$PROXY" ;;
    esac
    export HTTP_PROXY="$PROXY"
    export HTTPS_PROXY="$PROXY"
    export ALL_PROXY="$PROXY"
    echo "使用代理: $PROXY"
fi

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "缺少命令: $1" >&2
        exit 1
    fi
}

require_dotnet_sdk() {
    require_command dotnet
    if [ -z "$(dotnet --list-sdks 2>/dev/null || true)" ]; then
        echo "dotnet 未安装 .NET SDK(只找到运行时); 请安装 .NET SDK, 或把含 SDK 的 dotnet 目录加入 PATH。" >&2
        exit 1
    fi
}

get_required_package_version() {
    local version
    version="$(sed -nE 's/.*Include="DryIoc\.dll"[[:space:]]+Version="([^"]+)".*/\1/p' "$CSPROJ" | head -n1)"
    if [ -z "$version" ]; then
        echo "无法从 $CSPROJ 读取 DryIoc.dll 的版本。" >&2
        exit 1
    fi
    printf '%s' "$version"
}

get_latest_commit() {
    require_command git
    local sha
    sha="$(git ls-remote "$REPO_URL" HEAD | head -n1 | cut -f1)"
    if [ -z "$sha" ]; then
        echo "无法从 $REPO_URL 解析最新 commit。" >&2
        exit 1
    fi
    printf '%s' "$sha"
}

# 远程不可达时输出空, 由调用方决定降级行为。
try_get_latest_commit() {
    git ls-remote "$REPO_URL" HEAD 2>/dev/null | head -n1 | cut -f1 || true
}

read_build_stamp() {
    local stamp_file="$1"
    [ -f "$stamp_file" ] && tr -d '[:space:]' < "$stamp_file" || true
}

write_build_stamp() {
    printf '%s' "$2" > "$1"
}

# 从 CI 产物下载; 无 gh CLI 或下载不到时返回非零, 由调用方回退。
fetch_from_gh_artifact() {
    local target_nupkg="$1"
    local stamp_file="$2"

    command -v gh >/dev/null 2>&1 || return 1
    gh auth status >/dev/null 2>&1 || return 1

    local runs
    runs="$(gh run list --repo "$GH_REPO" --workflow "$GH_WORKFLOW" --status success --limit 20 --json databaseId,headSha --jq '.[] | "\(.databaseId) \(.headSha)"')"

    local run_id head_sha
    while read -r run_id head_sha; do
        [ -n "$run_id" ] || continue
        local artifact_names
        artifact_names="$(gh api "repos/$GH_REPO/actions/runs/$run_id/artifacts" --jq '.artifacts[] | select(.expired == false) | .name' 2>/dev/null || true)"
        printf '%s\n' "$artifact_names" | grep -qx "$GH_ARTIFACT" || continue

        rm -rf "$WORK_DIR"
        mkdir -p "$WORK_DIR"
        if gh run download "$run_id" --repo "$GH_REPO" --name "$GH_ARTIFACT" --dir "$WORK_DIR" >/dev/null 2>&1 \
            && [ -f "$WORK_DIR/$(basename -- "$target_nupkg")" ]; then
            mkdir -p "$FEED_DIR"
            cp "$WORK_DIR/$(basename -- "$target_nupkg")" "$target_nupkg"
            write_build_stamp "$stamp_file" "$head_sha"
            rm -rf "$WORK_DIR"
            return 0
        fi
        rm -rf "$WORK_DIR"
        return 1
    done <<< "$runs"
    return 1
}

# 在指定 commit 上本地构建; 与 CI 一样产出包 ID 为 DryIoc.dll 的 nupkg。
build_from_source() {
    local commit="$1"
    local stamp_file="$2"

    require_dotnet_sdk

    rm -rf "$WORK_DIR"
    mkdir -p "$WORK_DIR"

    echo "从源码构建 $PACKAGE_ID (commit ${commit:0:12})..."
    git clone --filter=blob:none --no-checkout "$REPO_URL" "$WORK_DIR"
    git -C "$WORK_DIR" fetch --depth 1 origin "$commit"
    git -C "$WORK_DIR" checkout --quiet "$commit"

    # 部分克隆(blob:none)下 SourceLink 读取 git 会失败, 需关闭版本查询。
    dotnet build "$WORK_DIR/src/DryIoc/DryIoc.csproj" -c Release \
        -p:LatestSupportedNet="$TARGET_FRAMEWORK" -p:TargetFrameworks="$TARGET_FRAMEWORK" \
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false

    mkdir -p "$FEED_DIR"
    dotnet pack "$WORK_DIR/src/DryIoc/DryIoc.csproj" -c Release --no-build \
        -p:LatestSupportedNet="$TARGET_FRAMEWORK" -p:TargetFrameworks="$TARGET_FRAMEWORK" \
        -p:NoWarn=NU5129 -p:TreatWarningsAsErrors=false \
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false \
        -o "$FEED_DIR"

    write_build_stamp "$stamp_file" "$commit"
    rm -rf "$WORK_DIR"
}

# 检查 feed 里是否出现与 csproj 要求不符的新版本, 给出明确提示。
assert_produced_version() {
    local target_nupkg="$1"
    local version="$2"

    [ -f "$target_nupkg" ] && return 0

    local produced
    produced="$(find "$FEED_DIR" -maxdepth 1 -name "$PACKAGE_ID.*.nupkg" -printf '%f\n' 2>/dev/null | head -n1 || true)"
    if [ -n "$produced" ]; then
        echo "上游最新产出的版本是 $produced, 与 csproj 要求的 $version 不一致; 请同步更新 $CSPROJ 的 PackageReference。" >&2
        exit 1
    fi
    echo "构建结束但未在 $FEED_DIR 找到任何 $PACKAGE_ID.*.nupkg, 请检查上面的输出。" >&2
    exit 1
}

PACKAGE_VERSION="$(get_required_package_version)"
TARGET_NUPKG="$FEED_DIR/$PACKAGE_ID.$PACKAGE_VERSION.nupkg"
STAMP_FILE="$TARGET_NUPKG.commit"

if [ -f "$TARGET_NUPKG" ] && [ "$FORCE" != "1" ]; then
    LOCAL_COMMIT="$(read_build_stamp "$STAMP_FILE")"
    REMOTE_COMMIT="$(try_get_latest_commit)"
    if [ -z "$REMOTE_COMMIT" ]; then
        if [ -z "$LOCAL_COMMIT" ]; then
            echo "警告: 本地包无 commit 戳记且远程不可达, 无法判断是否过期, 保留现有包: $TARGET_NUPKG" >&2
        else
            echo "远程不可达, 保留现有包(构建自 ${LOCAL_COMMIT:0:12}): $TARGET_NUPKG"
        fi
        exit 0
    fi
    if [ -n "$LOCAL_COMMIT" ] && [ "$LOCAL_COMMIT" = "$REMOTE_COMMIT" ]; then
        echo "本地 feed 已是最新(${REMOTE_COMMIT:0:12}): $TARGET_NUPKG"
        exit 0
    fi
    if [ -z "$LOCAL_COMMIT" ]; then
        echo "本地包无 commit 戳记, 重新构建以与远程 ${REMOTE_COMMIT:0:12} 对齐..."
    else
        echo "远程已更新(${LOCAL_COMMIT:0:12} -> ${REMOTE_COMMIT:0:12}), 重新构建..."
    fi
fi

if [ "$USE_GH_ARTIFACT" = "1" ]; then
    if fetch_from_gh_artifact "$TARGET_NUPKG" "$STAMP_FILE"; then
        echo "已从 CI 产物写入本地 feed: $TARGET_NUPKG"
        exit 0
    fi
    echo "gh CLI 不可用或 CI 产物下载失败, 回退到源码构建。" >&2
fi

COMMIT="$(get_latest_commit)"
build_from_source "$COMMIT" "$STAMP_FILE"
assert_produced_version "$TARGET_NUPKG" "$PACKAGE_VERSION"

echo "已写入本地 feed: $TARGET_NUPKG"
echo "下一步: dotnet restore"
