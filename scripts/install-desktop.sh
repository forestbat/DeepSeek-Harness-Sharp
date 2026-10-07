# 把 GUI 挂到当前用户的桌面环境里: 新建 .desktop 启动项(GNOME/KDE 等都能"下自己换")。
# 用法: bash scripts/install-desktop.sh [--target /path/to/dsh-gui] [--icon /path/to/icon.png]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target=""
icon=""

while [ $# -gt 0 ]; do
  case "$1" in
    --target) target="${2:-}"; shift 2 ;;
    --icon) icon="${2:-}"; shift 2 ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
done

if [ -z "$target" ]; then
  for candidate in \
    "$repo_root/DshGuiHost/bin/Release/net10.0/dsh-gui" \
    "$repo_root/DshGuiHost/bin/Debug/net10.0/dsh-gui" \
    "$repo_root/artifacts020/gui/dsh-gui"; do
    if [ -x "$candidate" ]; then
      target="$candidate"
      break
    fi
  done
fi

if [ -z "$target" ] || [ ! -x "$target" ]; then
  echo "找不到可执行的 dsh-gui; 先构建 DshGuiHost 或用 --target 指定路径" >&2
  exit 1
fi

if [ -z "$icon" ] || [ ! -f "$icon" ]; then
  icon="$repo_root/src/Dsh.Gui/Assets/icon.png"
fi

target="$(cd "$(dirname "$target")" && pwd)/$(basename "$target")"
icon_dir="$HOME/.local/share/icons/hicolor/256x256/apps"
icon_path="$icon_dir/dsh-gui.png"
applications_dir="$HOME/.local/share/applications"
desktop_file="$applications_dir/dsh-gui.desktop"

mkdir -p "$icon_dir" "$applications_dir"
if [ -f "$icon" ]; then
  cp "$icon" "$icon_path"
else
  icon_path=""
fi

cat > "$desktop_file" <<EOF
[Desktop Entry]
Type=Application
Name=DeepSeek Harness
Comment=DeepSeek Harness 图形界面
# dsh-gui 启动器自己会补上 `gui` 子命令, 这里不要再传一次。
Exec=$target
Icon=$icon_path
Terminal=false
Categories=Development;
StartupWMClass=dsh-gui
EOF

chmod +x "$desktop_file"
echo "已写入 $desktop_file"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$applications_dir" >/dev/null 2>&1 || true
fi
echo "目标: $target gui"
echo "图标: ${icon_path:-未设置}"
