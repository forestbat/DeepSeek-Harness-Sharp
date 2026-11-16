#!/usr/bin/env bash
# Dispatch the interactive SSH workflow, wait for each runner's handoff artifact and write an ssh config for it.
# Usage: open-session.sh --repo OWNER/REPO --key PRIVATE_KEY [--os windows-latest] [--ref BRANCH] [--timeout 60] [--out DIR]
set -euo pipefail

repo='' key='' os='windows-latest' ref='' timeout=60 out=''
while [ $# -gt 0 ]; do
  case "$1" in
    --repo) repo=$2; shift 2 ;;
    --key) key=$2; shift 2 ;;
    --os) os=$2; shift 2 ;;
    --ref) ref=$2; shift 2 ;;
    --timeout) timeout=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$repo" ] && [ -r "$key" ] || { echo "--repo and a readable --key are required" >&2; exit 2; }

case "$os" in
  all) runners=(ubuntu-latest windows-latest macos-latest) ;;
  *) runners=("$os") ;;
esac

started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
gh workflow run interactive.yml -R "$repo" -f os="$os" -f ref="$ref" -f timeout_minutes="$timeout"

run=''
until [ -n "$run" ]; do
  sleep 5
  run=$(gh run list -R "$repo" --workflow interactive.yml --event workflow_dispatch --limit 5 \
    --json databaseId,createdAt --jq "[.[] | select(.createdAt >= \"$started\")] | last | .databaseId // empty")
done
echo "run: https://github.com/$repo/actions/runs/$run"

out=${out:-${TMPDIR:-/tmp}/interactive-ssh-$run}
mkdir -p "$out"
printf '%s\n' \
  '@cert-authority uptermd.upterm.dev ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICiecex8Dq718eSe1CCLgLvDmI7AagvCtax7brPFWkh4' \
  '@cert-authority [uptermd.upterm.dev]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICiecex8Dq718eSe1CCLgLvDmI7AagvCtax7brPFWkh4' \
  > "$out/known_hosts"
: > "$out/ssh_config"

for runner in "${runners[@]}"; do
  until gh run download "$run" -R "$repo" -n "ssh-$runner" -D "$out/$runner" > /dev/null 2>&1; do
    if [ "$(gh run view "$run" -R "$repo" --json status --jq .status)" = completed ]; then
      echo "run $run ended before $runner handed off a session" >&2
      exit 1
    fi
    sleep 10
  done
  target=$(awk '{print $2}' "$out/$runner/ssh-command.txt")
  cat >> "$out/ssh_config" <<EOF
Host $runner
  HostName ${target#*@}
  User ${target%@*}
  IdentityFile $key
  IdentitiesOnly yes
  IdentityAgent none
  BatchMode yes
  UserKnownHostsFile $out/known_hosts
  StrictHostKeyChecking yes
  ServerAliveInterval 30

EOF
  echo "$runner: ssh -F $out/ssh_config $runner   (workspace: $(cat "$out/$runner/workspace.txt"))"
done
chmod 600 "$out/ssh_config"
