#!/usr/bin/env bash
# Explicitly provision the optional native test dependency from one verified source revision.
set -euo pipefail

SCRIPT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/fetch-iroh-ssh.sh"
ROOT_DIR="$(cd -- "$(dirname -- "$SCRIPT")/.." && pwd)"
REVISION=82e99719f8b4e44c69622140f4acb25e5ae54184
SOURCE_SHA256=cfcafde6b789b076bd6e4c28abbc9a92476db122efa5d644bdded35380d6a8ac
PATCH_SHA256=e383bd31cfe47d8ac57bb732ee53f31f24f6d94e8ec48d5a41cd59d26ed4f58c
PATCH="$ROOT_DIR/scripts/iroh-ssh-readiness.patch"
TOOLCHAIN=1.93.0
LOCKED=0
if [ "${1:-}" = --under-lock ]; then LOCKED=1; shift; fi
if [ "$#" -ne 1 ]; then
    echo "Usage: fetch-iroh-ssh.sh <linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64|win-arm64>" >&2
    exit 2
fi
RID="$1"
case "$RID" in
    linux-x64) TARGET=x86_64-unknown-linux-gnu ;;
    linux-arm64) TARGET=aarch64-unknown-linux-gnu ;;
    osx-x64) TARGET=x86_64-apple-darwin ;;
    osx-arm64) TARGET=aarch64-apple-darwin ;;
    win-x64) TARGET=x86_64-pc-windows-msvc ;;
    win-arm64) TARGET=aarch64-pc-windows-msvc ;;
    *) echo "Unsupported iroh-ssh RID: $RID" >&2; exit 2 ;;
esac
FILE=iroh-ssh
case "$RID" in win-*) FILE=iroh-ssh.exe ;; esac
WORK_DIR="$ROOT_DIR/artifacts/iroh-ssh/$RID"
OUTPUT="$WORK_DIR/$FILE"
STAMP="$WORK_DIR/build.sha256"
mkdir -p "$WORK_DIR"
if [ "$LOCKED" -eq 0 ]; then
    command -v python3 >/dev/null 2>&1 || { echo 'iroh-ssh acquisition requires Python 3 for a portable POSIX process lock.' >&2; exit 1; }
    # POSIX record locks survive exec but are not inherited by forked compiler processes.
    exec python3 -c 'import fcntl, os, sys
fd = os.open(sys.argv[1], os.O_CREAT | os.O_RDWR, 0o600)
fcntl.lockf(fd, fcntl.LOCK_EX)
os.set_inheritable(fd, True)
os.execvp("bash", ["bash", sys.argv[2], "--under-lock", sys.argv[3]])' "$WORK_DIR/.acquire.lock" "$SCRIPT" "$RID"
fi

sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d ' ' -f 1
    else
        shasum -a 256 "$1" | cut -d ' ' -f 1
    fi
}

[ "$(sha256 "$PATCH")" = "$PATCH_SHA256" ] || { echo 'iroh-ssh readiness patch SHA256 mismatch.' >&2; exit 1; }
cp "$ROOT_DIR/scripts/iroh-ssh.NOTICE.txt" "$WORK_DIR/NOTICE.txt"
if [ -f "$OUTPUT" ] && [ -f "$STAMP" ] && [ -f "$WORK_DIR/LICENSE.txt" ] && [ -f "$WORK_DIR/THIRD-PARTY-LICENSES.txt" ] && [ -f "$WORK_DIR/READINESS.patch" ] && [ "$(sha256 "$WORK_DIR/READINESS.patch")" = "$PATCH_SHA256" ]; then
    EXPECTED="v4 $REVISION $PATCH_SHA256 $TOOLCHAIN $TARGET $(sha256 "$OUTPUT") $(sha256 "$WORK_DIR/LICENSE.txt") $(sha256 "$WORK_DIR/THIRD-PARTY-LICENSES.txt")"
    if [ "$(cat "$STAMP")" = "$EXPECTED" ]; then
        case "$RID" in win-*) ;; *) chmod 755 "$OUTPUT" ;; esac
        exit 0
    fi
fi
for COMMAND in curl tar rustup cargo jq git; do
    command -v "$COMMAND" >/dev/null 2>&1 || { echo "Building the optional iroh-ssh test dependency requires $COMMAND." >&2; exit 1; }
done
STAGE_DIR="$(mktemp -d "$WORK_DIR/.stage.XXXXXXXX")"
trap 'rm -rf -- "$STAGE_DIR"' EXIT
HOST_OS="$(uname -s)"
HOST_ARCH="$(uname -m)"
case "$HOST_ARCH" in x86_64|amd64) HOST_ARCH=x64 ;; aarch64|arm64) HOST_ARCH=arm64 ;; *) echo "Unsupported build host architecture: $HOST_ARCH" >&2; exit 1 ;; esac
case "$HOST_OS:$RID" in
    Linux:linux-*)
        if [ "$RID" = "linux-$HOST_ARCH" ]; then
            CC_NAME=cc
            AR_NAME=ar
        else
            case "$RID" in linux-arm64) PREFIX=aarch64-linux-gnu ;; linux-x64) PREFIX=x86_64-linux-gnu ;; esac
            CC_NAME="$PREFIX-gcc"
            AR_NAME="$PREFIX-ar"
        fi
        COMPILER="$(command -v "$CC_NAME")" || { echo "Install $CC_NAME and its glibc development sysroot to build $RID on Linux/$HOST_ARCH." >&2; exit 1; }
        ARCHIVER="$(command -v "$AR_NAME")" || { echo "Install $AR_NAME for $RID." >&2; exit 1; }
        COMPILER_TARGET="$("$COMPILER" -dumpmachine)"
        case "$RID:$COMPILER_TARGET" in linux-x64:x86_64*linux*|linux-arm64:aarch64*linux*) ;; *) echo "Compiler $COMPILER targets $COMPILER_TARGET, not $RID." >&2; exit 1 ;; esac
        CRT="$("$COMPILER" -print-file-name=crt1.o)"
        LIBC="$("$COMPILER" -print-file-name=libc.so)"
        [ -f "$CRT" ] && [ -f "$LIBC" ] || { echo "Install the glibc development sysroot for $CC_NAME (crt1.o and libc.so are required)." >&2; exit 1; }
        ;;
    Darwin:osx-*)
        command -v xcrun >/dev/null 2>&1 || { echo 'Install Xcode command-line tools and the macOS SDK.' >&2; exit 1; }
        CLANG="$(xcrun --sdk macosx --find clang)"
        SDK="$(xcrun --sdk macosx --show-sdk-path)"
        ARCHIVER="$(xcrun --sdk macosx --find ar)"
        [ -x "$CLANG" ] && [ -x "$ARCHIVER" ] && [ -d "$SDK/usr/lib" ] || { echo 'The selected Xcode macOS compiler/SDK is incomplete.' >&2; exit 1; }
        case "$RID" in osx-x64) CPU=x86_64 ;; osx-arm64) CPU=arm64 ;; esac
        COMPILER="$STAGE_DIR/macos-cc"
        printf '#!/usr/bin/env bash\nexec %q -arch %q -isysroot %q "$@"\n' "$CLANG" "$CPU" "$SDK" > "$COMPILER"
        chmod 755 "$COMPILER"
        ;;
    *)
        echo "Cannot build $RID on $HOST_OS/$HOST_ARCH: use Linux with its matching GNU cross-compiler/sysroot, macOS with Xcode for osx RIDs, or fetch-iroh-ssh.ps1 on Windows with MSVC for win RIDs." >&2
        exit 1
        ;;
esac
COMPILER_TOML="$(jq -Rn --arg value "$COMPILER" '$value')"
ARCHIVER_TOML="$(jq -Rn --arg value "$ARCHIVER" '$value')"
TARGET_KEY="${TARGET//-/_}"
CARGO_CONFIG_FILE="$STAGE_DIR/cargo.toml"
printf '[target.%s]\nlinker = %s\n\n[env.CC_%s]\nvalue = %s\nforce = true\n\n[env.AR_%s]\nvalue = %s\nforce = true\n' \
    "$TARGET" "$COMPILER_TOML" "$TARGET_KEY" "$COMPILER_TOML" "$TARGET_KEY" "$ARCHIVER_TOML" > "$CARGO_CONFIG_FILE"
ARCHIVE="$WORK_DIR/source.tar.gz"
if [ ! -f "$ARCHIVE" ] || [ "$(sha256 "$ARCHIVE")" != "$SOURCE_SHA256" ]; then
    curl --fail --location --proto '=https' --tlsv1.2 --output "$STAGE_DIR/source.tar.gz" "https://codeload.github.com/rustonbsd/iroh-ssh/tar.gz/$REVISION"
    [ "$(sha256 "$STAGE_DIR/source.tar.gz")" = "$SOURCE_SHA256" ] || { echo 'iroh-ssh source archive SHA256 mismatch.' >&2; exit 1; }
    mv -f "$STAGE_DIR/source.tar.gz" "$ARCHIVE"
fi
rustup toolchain install "$TOOLCHAIN" --profile minimal
rustup target add --toolchain "$TOOLCHAIN" "$TARGET"
tar -xzf "$ARCHIVE" -C "$STAGE_DIR"
SOURCE_DIR="$WORK_DIR/iroh-ssh-$REVISION"
rm -rf -- "$SOURCE_DIR"
mv "$STAGE_DIR/iroh-ssh-$REVISION" "$SOURCE_DIR"
git -C "$ROOT_DIR" apply --directory="artifacts/iroh-ssh/$RID/iroh-ssh-$REVISION" "$PATCH"
cargo "+$TOOLCHAIN" --config "$CARGO_CONFIG_FILE" build --locked --release --target "$TARGET" --manifest-path "$SOURCE_DIR/Cargo.toml" --target-dir "$WORK_DIR/target"
cp "$WORK_DIR/target/$TARGET/release/$FILE" "$STAGE_DIR/$FILE"
python3 - "$STAGE_DIR/$FILE" "$RID" <<'PY'
import struct, sys
with open(sys.argv[1], "rb") as executable:
    header = executable.read(64)
rid = sys.argv[2]
if rid.startswith("linux-"):
    expected = 62 if rid == "linux-x64" else 183
    valid = header[:6] == b"\x7fELF\x02\x01" and struct.unpack_from("<H", header, 18)[0] == expected
else:
    expected = 0x01000007 if rid == "osx-x64" else 0x0100000c
    valid = header[:4] == b"\xcf\xfa\xed\xfe" and struct.unpack_from("<I", header, 4)[0] == expected
if not valid:
    raise SystemExit(f"The compiled component architecture does not match {rid}.")
PY
chmod 755 "$STAGE_DIR/$FILE"
cp "$SOURCE_DIR/LICENSE" "$STAGE_DIR/LICENSE.txt"
cp "$PATCH" "$STAGE_DIR/READINESS.patch"
cargo "+$TOOLCHAIN" metadata --locked --filter-platform "$TARGET" --format-version 1 --manifest-path "$SOURCE_DIR/Cargo.toml" > "$STAGE_DIR/dependencies.json"
jq -r '.packages[] | [.name, .version, (.license // "unspecified"), .manifest_path, (.source // "local")] | @tsv' "$STAGE_DIR/dependencies.json" > "$STAGE_DIR/dependencies.tsv"
{
    printf 'Corresponding source for each version-locked Rust crate is available at the download URL below.\n'
    printf 'For MPL-2.0 components, including attohttpc, these links provide the corresponding source covered by that license.\n'
    printf 'iroh-ssh modifications: see READINESS.patch next to this file (SHA256 %s).\n' "$PATCH_SHA256"
    while IFS=$'\t' read -r NAME VERSION LICENSE MANIFEST SOURCE; do
        printf '\n=== %s %s (%s) ===\n' "$NAME" "$VERSION" "$LICENSE"
        if [ "$NAME" = iroh-ssh ] && [ "$SOURCE" = local ]; then
            printf 'Corresponding source: https://github.com/rustonbsd/iroh-ssh/archive/%s.tar.gz\n' "$REVISION"
        elif [[ "$SOURCE" = registry+* ]]; then
            printf 'Corresponding source: https://crates.io/api/v1/crates/%s/%s/download\n' "$NAME" "$VERSION"
        else
            echo "Unrecognized locked dependency source for $NAME: $SOURCE" >&2
            exit 1
        fi
        PACKAGE_DIR="$(dirname -- "$MANIFEST")"
        while IFS= read -r -d '' LICENSE_FILE; do
            printf '\n--- %s ---\n' "${LICENSE_FILE#"$PACKAGE_DIR/"}"
            cat "$LICENSE_FILE"
            printf '\n'
        done < <(find "$PACKAGE_DIR" -maxdepth 2 -type f \( -iname 'LICENSE*' -o -iname 'COPYING*' -o -iname 'NOTICE*' \) -print0)
    done < "$STAGE_DIR/dependencies.tsv"
} > "$STAGE_DIR/THIRD-PARTY-LICENSES.txt"
printf 'v4 %s %s %s %s %s %s %s\n' "$REVISION" "$PATCH_SHA256" "$TOOLCHAIN" "$TARGET" "$(sha256 "$STAGE_DIR/$FILE")" "$(sha256 "$STAGE_DIR/LICENSE.txt")" "$(sha256 "$STAGE_DIR/THIRD-PARTY-LICENSES.txt")" > "$STAGE_DIR/build.sha256"
for GENERATED in "$FILE" LICENSE.txt THIRD-PARTY-LICENSES.txt READINESS.patch; do mv -f "$STAGE_DIR/$GENERATED" "$WORK_DIR/$GENERATED"; done
mv -f "$STAGE_DIR/build.sha256" "$STAMP"
