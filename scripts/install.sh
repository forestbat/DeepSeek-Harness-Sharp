#!/usr/bin/env bash
#
# DeepSeek Harness 命令行安装脚本(Linux/macOS)。
# 从 GitHub Release 下载对应平台的构建, 装到用户目录, 并注册 `dsharp` 命令。
#
# 用法:
#   curl -fsSL https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.sh | bash
#   curl -fsSL <url> | bash -s -- --version 0.1.0
#
# 可用环境变量(命令行参数优先):
#   DSHARP_VERSION        版本号或 latest(默认 latest)
#   DSHARP_HOME           安装目录(默认 $HOME/.dsharp)
#   DSHARP_BIN_DIR        命令目录(默认 $HOME/.local/bin)
#   DSHARP_BASE_URL       Release 基址(默认 GitHub Release, 可指向镜像)
#   DSHARP_LOCAL_ARCHIVE  本地归档路径, 跳过下载(离线安装/测试用)
#   DSHARP_PROXY          下载代理, 例如 http://127.0.0.1:10808
#
# Release 资产命名约定: dsharp-<rid>.tar.gz, rid 形如 linux-x64 / linux-arm64 / osx-x64 / osx-arm64。
# 归档根目录即发布输出(含可执行文件 DeepSeek-Harness-Sharp 与 plugins/)。
set -euo pipefail

REPO="forestbat/DeepSeek-Harness-Sharp"
VERSION="${DSHARP_VERSION:-latest}"
INSTALL_DIR="${DSHARP_HOME:-$HOME/.dsharp}"
BIN_DIR="${DSHARP_BIN_DIR:-$HOME/.local/bin}"
BASE_URL="${DSHARP_BASE_URL:-https://github.com/$REPO/releases}"
LOCAL_ARCHIVE="${DSHARP_LOCAL_ARCHIVE:-}"
PROXY="${DSHARP_PROXY:-}"
COMMAND_NAME="dsharp"
HOST_BINARY="DeepSeek-Harness-Sharp"

log() { printf '%s\n' "$*"; }
fail() { printf '安装失败: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<'EOF'
用法: install.sh [选项]
  --version <v>        版本号或 latest
  --home <dir>         安装目录
  --bin-dir <dir>      命令目录
  --base-url <url>     Release 基址
  --local-archive <f>  本地归档路径(跳过下载)
  -h, --help           显示本帮助
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version 需要一个值}"; shift 2 ;;
        --version=*) VERSION="${1#--version=}"; shift ;;
        --home) INSTALL_DIR="${2:?--home 需要一个值}"; shift 2 ;;
        --home=*) INSTALL_DIR="${1#--home=}"; shift ;;
        --bin-dir) BIN_DIR="${2:?--bin-dir 需要一个值}"; shift 2 ;;
        --bin-dir=*) BIN_DIR="${1#--bin-dir=}"; shift ;;
        --base-url) BASE_URL="${2:?--base-url 需要一个值}"; shift 2 ;;
        --base-url=*) BASE_URL="${1#--base-url=}"; shift ;;
        --local-archive) LOCAL_ARCHIVE="${2:?--local-archive 需要一个值}"; shift 2 ;;
        --local-archive=*) LOCAL_ARCHIVE="${1#--local-archive=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) fail "未知参数: $1" ;;
    esac
done

detect_os() {
    case "$(uname -s)" in
        Linux*) printf 'linux' ;;
        Darwin*) printf 'osx' ;;
        *) fail "不支持的操作系统: $(uname -s)" ;;
    esac
}

detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64) printf 'x64' ;;
        arm64|aarch64) printf 'arm64' ;;
        *) fail "不支持的 CPU 架构: $(uname -m)" ;;
    esac
}

RID="$(detect_os)-$(detect_arch)"
ASSET="dsharp-$RID.tar.gz"

asset_url() {
    case "$VERSION" in
        latest) printf '%s/latest/download/%s' "$BASE_URL" "$ASSET" ;;
        *)
            local tag="$VERSION"
            case "$tag" in v*) ;; *) tag="v$tag" ;; esac
            printf '%s/download/%s/%s' "$BASE_URL" "$tag" "$ASSET"
            ;;
    esac
}

if [ -n "$PROXY" ]; then
    export http_proxy="$PROXY"
    export https_proxy="$PROXY"
fi

download() {
    local url="$1"; local dest="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fL --retry 3 -o "$dest" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -O "$dest" "$url"
    else
        fail "需要 curl 或 wget"
    fi
}

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

ARCHIVE="$TMP_DIR/$ASSET"
if [ -n "$LOCAL_ARCHIVE" ]; then
    [ -f "$LOCAL_ARCHIVE" ] || fail "本地归档不存在: $LOCAL_ARCHIVE"
    cp "$LOCAL_ARCHIVE" "$ARCHIVE"
    log "使用本地归档: $LOCAL_ARCHIVE"
else
    URL="$(asset_url)"
    log "下载 $URL"
    download "$URL" "$ARCHIVE" || fail "下载失败, 请检查版本号、网络或 DSHARP_PROXY"
fi

EXTRACT_DIR="$TMP_DIR/extract"
mkdir -p "$EXTRACT_DIR"
tar -xzf "$ARCHIVE" -C "$EXTRACT_DIR" || fail "解压失败: $ARCHIVE"

HOST_PATH="$(find "$EXTRACT_DIR" -type f -name "$HOST_BINARY" -print | head -n1)"
[ -n "$HOST_PATH" ] || fail "归档里找不到 $HOST_BINARY"
SRC_DIR="$(cd "$(dirname "$HOST_PATH")" && pwd)"

rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
cp -R "$SRC_DIR/." "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/$HOST_BINARY"

mkdir -p "$BIN_DIR"
if ! ln -sf "$INSTALL_DIR/$HOST_BINARY" "$BIN_DIR/$COMMAND_NAME" 2>/dev/null; then
    cat > "$BIN_DIR/$COMMAND_NAME" <<EOF
#!/usr/bin/env bash
exec "$INSTALL_DIR/$HOST_BINARY" "\$@"
EOF
    chmod +x "$BIN_DIR/$COMMAND_NAME"
fi

# 命令目录不在 PATH 时, 追加到常见 shell 的启动文件(只加一次)。
if ! printf '%s' ":$PATH:" | grep -q ":$BIN_DIR:"; then
    LINE="export PATH=\"$BIN_DIR:\$PATH\""
    WROTE=0
    for rc in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
        [ -f "$rc" ] || continue
        if grep -qF "$LINE" "$rc" 2>/dev/null; then
            WROTE=1
            continue
        fi
        printf '\n# DeepSeek Harness\n%s\n' "$LINE" >> "$rc"
        WROTE=1
    done
    if [ "$WROTE" = "0" ]; then
        printf '\n# DeepSeek Harness\n%s\n' "$LINE" >> "$HOME/.profile"
    fi
    log "已把 $BIN_DIR 写入 shell 启动文件; 新开终端或执行: export PATH=\"$BIN_DIR:\$PATH\""
fi

if command -v dshsh >/dev/null 2>&1; then
    log "提示: 检测到旧命令 'dshsh'($(command -v dshsh)), 新版命令是 'dsharp'; 可删除旧的。"
fi

log ""
log "已安装 $COMMAND_NAME -> $INSTALL_DIR/$HOST_BINARY"
log "版本: $VERSION (平台 $RID)"
log "直接运行: $COMMAND_NAME"
