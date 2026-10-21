#!/usr/bin/env bash
# Exercise the LAN-only lifecycle in macOS's documented, non-admin SSH context.
# LaunchAgents need the user's Local Network grant; CI must not impersonate consent.
set -euo pipefail

[ "$(uname -s)" = Darwin ] || { echo 'This acceptance requires macOS.' >&2; exit 1; }
[ "$(id -u)" -ne 0 ] || { echo 'Run the acceptance launcher as the CI user, not root.' >&2; exit 1; }
ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="$ROOT_DIR/artifacts/iroh-macos-lan"
IROH_COMMAND="$(command -v iroh-ssh)"
[ "${IROH_COMMAND#/}" != "$IROH_COMMAND" ] && [ -x "$IROH_COMMAND" ] || { echo 'Install the explicit native Iroh test fixture on PATH first.' >&2; exit 1; }
IROH_DIRECTORY="$(dirname -- "$IROH_COMMAND")"
ACCOUNT=dshirohci
ACCOUNT_UID=1001
SSH_PORT=22223
OWNED_USER=0
SSHD_LABEL="org.dsharp.tests.iroh-lan.$$"
OWNED_DAEMON=0
DIRECTORY_ACCESS="$ACCOUNT allow list,search,add_subdirectory,add_file,delete_child"
[ -z "$(dscl . -search /Users RecordName "$ACCOUNT")" ] || { echo 'The acceptance account already exists.' >&2; exit 1; }
[ -z "$(dscl . -search /Users UniqueID "$ACCOUNT_UID")" ] || { echo 'The acceptance UID is already allocated.' >&2; exit 1; }
mkdir -p "$WORK_DIR/home" "$ROOT_DIR/.daemon-tests" "$ROOT_DIR/artifacts020/test-homes"
cleanup() {
    local code=$?
    if [ "$code" -ne 0 ] && [ -f "$WORK_DIR/sshd.log" ]; then cat "$WORK_DIR/sshd.log"; fi
    if [ "$OWNED_DAEMON" -eq 1 ]; then sudo launchctl bootout "system/$SSHD_LABEL" 2>/dev/null || true; fi
    if [ "$OWNED_USER" -eq 1 ]; then
        sudo pkill -u "$ACCOUNT_UID" 2>/dev/null || true
        chmod -a "$DIRECTORY_ACCESS" "$ROOT_DIR/.daemon-tests" "$ROOT_DIR/artifacts020/test-homes" 2>/dev/null || true
        sudo dscl . -delete "/Users/$ACCOUNT"
    fi
    sudo rm -rf -- "$WORK_DIR"
    exit "$code"
}
trap cleanup EXIT
sudo dscl . -create "/Users/$ACCOUNT"
OWNED_USER=1
sudo dscl . -create "/Users/$ACCOUNT" UniqueID "$ACCOUNT_UID"
sudo dscl . -create "/Users/$ACCOUNT" PrimaryGroupID 20
sudo dscl . -create "/Users/$ACCOUNT" UserShell /bin/bash
sudo dscl . -create "/Users/$ACCOUNT" NFSHomeDirectory "$WORK_DIR/home"
# Public-key-only SSH still needs a usable account, not a disabled password record.
sudo dscl . -passwd "/Users/$ACCOUNT" "$(openssl rand -hex 32)"
sudo chown "$ACCOUNT_UID:20" "$WORK_DIR/home"
chmod +a "$DIRECTORY_ACCESS" "$ROOT_DIR/.daemon-tests" "$ROOT_DIR/artifacts020/test-homes"
ssh-keygen -q -t ed25519 -N '' -C iro-lan-host -f "$WORK_DIR/host_key"
ssh-keygen -q -t ed25519 -N '' -C iro-lan-client -f "$WORK_DIR/client_key"
printf '[127.0.0.1]:%s ' "$SSH_PORT" > "$WORK_DIR/known_hosts"
ssh-keygen -y -f "$WORK_DIR/host_key" >> "$WORK_DIR/known_hosts"
cat > "$WORK_DIR/sshd.conf" <<EOF
Port $SSH_PORT
ListenAddress 127.0.0.1
HostKey "$WORK_DIR/host_key"
PidFile "$WORK_DIR/sshd.pid"
AuthorizedKeysFile "$WORK_DIR/client_key.pub"
AllowUsers $ACCOUNT
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
SetEnv DOTNET_ROOT=${DOTNET_ROOT:?setup-dotnet must provide DOTNET_ROOT}
EOF
# A launchd daemon supplies the real system SSH context documented by TN3179.
python3 - "$WORK_DIR" "$SSHD_LABEL" <<'PY'
import plistlib
import sys
from pathlib import Path

root = Path(sys.argv[1])
with (root / "sshd.plist").open("wb") as stream:
    plistlib.dump({
        "Label": sys.argv[2],
        "ProgramArguments": ["/usr/sbin/sshd", "-D", "-e", "-f", str(root / "sshd.conf")],
        "RunAtLoad": True,
        "StandardOutPath": str(root / "sshd.log"),
        "StandardErrorPath": str(root / "sshd.log"),
    }, stream)
PY
sudo chown root:wheel "$WORK_DIR/sshd.plist"
sudo chmod 644 "$WORK_DIR/sshd.plist"
if sudo launchctl print "system/$SSHD_LABEL" >/dev/null 2>&1; then
    echo 'The acceptance SSH daemon already exists.' >&2
    exit 1
fi
OWNED_DAEMON=1
sudo launchctl bootstrap system "$WORK_DIR/sshd.plist"
for attempt in {1..20}; do
    if ssh-keyscan -T 1 -p "$SSH_PORT" 127.0.0.1 >/dev/null 2>&1; then break; fi
    sudo launchctl print "system/$SSHD_LABEL" >/dev/null
    sleep 0.1
done
printf -v command 'test "$(id -u)" -ne 0 && ! launchctl print "gui/$(id -u)" >/dev/null 2>&1 && export PATH=%q:"$PATH" && cd %q && exec %q -noColor -failSkips -method Dsh.Tests.IrohReceiverEndToEndTests.TargetCli_Lan_Readiness_And_Restart_Work_Without_A_Relay' \
    "$IROH_DIRECTORY" "$ROOT_DIR" "$ROOT_DIR/tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests"
ssh -F /dev/null -i "$WORK_DIR/client_key" -o IdentitiesOnly=yes -o BatchMode=yes \
    -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$WORK_DIR/known_hosts" \
    -o GlobalKnownHostsFile=/dev/null -p "$SSH_PORT" "$ACCOUNT@127.0.0.1" "$command"
