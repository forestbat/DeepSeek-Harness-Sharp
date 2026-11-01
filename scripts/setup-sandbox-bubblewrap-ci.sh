#!/usr/bin/env bash
set -euo pipefail
umask 077

# This unprivileged dependency build is only for the hosted Linux native test gate.
test "${GITHUB_ACTIONS:-}" = true
test "${RUNNER_OS:-}" = Linux
test -n "${GITHUB_PATH:-}"
version=0.13.1
digest=0e85c8702248b8dd64144f594147553650cdd0e4c951690345faace79c217c4d
root="$PWD/.artifacts/sandbox-bubblewrap-ci"
mkdir -p .artifacts
mkdir "$root"
root="$(realpath "$root")"
archive="$root/bubblewrap-$version.tar.xz"
curl --fail --location --proto '=https' --tlsv1.2 \
  "https://github.com/containers/bubblewrap/releases/download/v$version/bubblewrap-$version.tar.xz" \
  --output "$archive"
printf '%s  %s\n' "$digest" "$archive" | sha256sum --check --status
tar -xJf "$archive" -C "$root"
# Upstream sources are never patched, installed system-wide, or made setuid.
meson setup "$root/build" "$root/bubblewrap-$version" \
  --prefix="$root/install" --buildtype=release --wrap-mode=nodownload \
  -Dtests=false -Dman=disabled -Dbash_completion=disabled -Dzsh_completion=disabled
meson compile -C "$root/build" --jobs 2
meson install -C "$root/build" --no-rebuild
test -x "$root/install/bin/bwrap"
test ! -u "$root/install/bin/bwrap"
test ! -g "$root/install/bin/bwrap"
getcap "$root/install/bin/bwrap" | while IFS= read -r capability; do
  test -z "$capability"
done
test "$("$root/install/bin/bwrap" --version)" = "bubblewrap $version"
help="$("$root/install/bin/bwrap" --help)"
for option in --bind-fd --ro-bind-fd --disable-userns --seccomp; do
  case "$help" in *"$option "*) ;; *) printf '%s\n' "Missing required bubblewrap option: $option" >&2; exit 1 ;; esac
done
test "$(realpath "$root/install/bin/bwrap")" = "$root/install/bin/bwrap"
sha256sum "$root/install/bin/bwrap" > "$root/bwrap.sha256"
if [ -r /proc/sys/kernel/apparmor_restrict_unprivileged_userns ] \
  && [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns)" = 1 ]; then
  # Permit namespace creation only for this exact run-owned executable, not the runner account.
  profile="$root/bwrap.apparmor"
  printf 'profile dsh-bwrap-ci-%s-%s "%s" flags=(unconfined) {\n  userns,\n}\n' \
    "${GITHUB_RUN_ID:?}" "${GITHUB_RUN_ATTEMPT:?}" "$root/install/bin/bwrap" > "$profile"
  sudo apparmor_parser --replace "$profile"
fi
printf '%s\n' "$root/install/bin" >> "$GITHUB_PATH"
