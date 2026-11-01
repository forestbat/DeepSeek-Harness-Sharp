#!/usr/bin/env bash
set -euo pipefail

# This CI-only launcher delegates one transient service, never the host cgroup root.
if [ "${RUNNER_OS:-}" = Linux ] && [ "${1:-}" != --delegated ]; then
  unit="dsh-sandbox-ci-${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}"
  environment=(--setenv="PATH=$PATH")
  if [ -n "${DOTNET_ROOT:-}" ]; then
    environment+=(--setenv="DOTNET_ROOT=$DOTNET_ROOT")
  fi
  exec sudo systemd-run --quiet --wait --pipe --collect --unit="$unit" \
    --service-type=exec --uid="$(id -u)" --gid="$(id -g)" \
    --property=Delegate=memory\ pids --property=MemoryMax=4G \
    --property=MemorySwapMax=0 --property=TasksMax=1024 \
    --working-directory="$PWD" "${environment[@]}" \
    --setenv=RUNNER_OS=Linux --setenv="DSH_SANDBOX_CI_UNIT=$unit" \
    /bin/bash "$PWD/scripts/test-sandbox-native.sh" --delegated
fi

if [ "${1:-}" = --delegated ]; then
  shift
  test "$(stat -fc %T /sys/fs/cgroup)" = cgroup2fs
  membership=
  while IFS=: read -r hierarchy controllers path; do
    if [ "$hierarchy" = 0 ] && [ -z "$controllers" ]; then membership="$path"; break; fi
  done < /proc/self/cgroup
  test "$membership" = "/system.slice/${DSH_SANDBOX_CI_UNIT:?}.service"
  delegation="/sys/fs/cgroup$membership"
  test "$(cat "$delegation/memory.max")" = 4294967296
  test "$(cat "$delegation/memory.swap.max")" = 0
  test "$(cat "$delegation/pids.max")" = 1024
  controllers=" $(cat "$delegation/cgroup.controllers") "
  case "$controllers" in *" memory "*) ;; *) exit 1 ;; esac
  case "$controllers" in *" pids "*) ;; *) exit 1 ;; esac
  # Move the launcher into a leaf before enabling the no-internal-process controllers.
  mkdir "$delegation/runner"
  printf '%s\n' "$$" > "$delegation/runner/cgroup.procs"
  printf '+memory +pids\n' > "$delegation/cgroup.subtree_control"
  probe="$delegation/prerequisite-probe"
  mkdir "$probe"
  for control in memory.max memory.swap.max memory.oom.group pids.max cgroup.kill; do
    test -w "$probe/$control"
  done
  printf '67108864\n' > "$probe/memory.max"
  printf '0\n' > "$probe/memory.swap.max"
  printf '1\n' > "$probe/memory.oom.group"
  printf '16\n' > "$probe/pids.max"
  test "$(cat "$probe/memory.max")" = 67108864
  test "$(cat "$probe/memory.swap.max")" = 0
  test "$(cat "$probe/memory.oom.group")" = 1
  test "$(cat "$probe/pids.max")" = 16
  printf '1\n' > "$probe/cgroup.kill"
  rmdir "$probe"
  test "$(command -v bwrap)" = "$(realpath .artifacts/sandbox-bubblewrap-ci/install/bin/bwrap)"
  sha256sum --check --status .artifacts/sandbox-bubblewrap-ci/bwrap.sha256
  test "$(bwrap --version)" = 'bubblewrap 0.13.1'
  help="$(bwrap --help)"
  for option in --bind-fd --ro-bind-fd --disable-userns --seccomp; do
    case "$help" in *"$option "*) ;; *) printf '%s\n' "Missing required bubblewrap option: $option" >&2; exit 1 ;; esac
  done
  test -x /usr/bin/python3
  test -x /usr/bin/perl
fi

exe="tests/Dsh.Tests/bin/Debug/net10.0/Dsh.Tests"
if [ "${RUNNER_OS:?}" = Windows ]; then
  # The authority probe leases only this owned deployment, never a system .NET installation.
  exe=".artifacts/sandbox-native-publish/Dsh.Tests.exe"
  test -s .artifacts/sandbox-native-publish/coreclr.dll
  test -s .artifacts/sandbox-native-publish/hostpolicy.dll
  test -s .artifacts/sandbox-native-publish/Profiles/Templates/settings.yaml
  for fixture in \
    isolated-plugin/Dsh.IsolatedPluginSample.dll \
    isolated-plugin/Dsh.IsolatedPluginSample.deps.json \
    isolated-plugin/Dsh.Plugins.Abstractions.dll \
    mcp-resource-server/Dsh.McpResourceServerSample.dll \
    mcp-resource-server/Dsh.McpResourceServerSample.deps.json \
    mcp-resource-server/Dsh.McpResourceServerSample.runtimeconfig.json \
    mcp-resource-server/ModelContextProtocol.dll; do
    test -s ".artifacts/sandbox-native-publish/$fixture"
  done
fi
test -x "$exe"
common=(-noColor -parallelMode none -failSkips -reporter verbose)
mkdir -p .artifacts/sandbox-ci-tmp
temporary="$PWD/.artifacts/sandbox-ci-tmp"
if [ "$RUNNER_OS" = Windows ]; then temporary="$(cygpath -w "$temporary")"; fi
export TMPDIR="$temporary" TMP="$temporary" TEMP="$temporary"

if [ "$RUNNER_OS" = macOS ]; then
  macos_native_start="$temporary/macos-native-start.$$"
  touch "$macos_native_start"
  collect_macos_native_failure() {
    status="$1"
    trap - EXIT
    set +e
    if [ "$status" -ne 0 ]; then
      for report in "$HOME/Library/Logs/DiagnosticReports"/{sandbox-exec,env,sh,bash,perl,nc,Dsh.Tests,dotnet,dsharp}*.ips; do
        if [ -f "$report" ] && [ "$report" -nt "$macos_native_start" ]; then
          jq -c 'select(has("procName")) | {procName,pid,parentPid,exception,termination,asi,faultingThread,frames: [.threads[.faultingThread // 0].frames[]? | {symbol,imageIndex}]}' "$report"
        fi
      done
      sudo /usr/bin/log show --last 2m --style compact --info --debug \
        --predicate 'eventMessage CONTAINS "Sandbox: env(" OR eventMessage CONTAINS "Sandbox: sh(" OR eventMessage CONTAINS "Sandbox: perl(" OR eventMessage CONTAINS "Sandbox: nc(" OR eventMessage CONTAINS "Sandbox: Dsh.Tests(" OR eventMessage CONTAINS "Sandbox: dotnet(" OR eventMessage CONTAINS "Sandbox: dsharp("'
    fi
    rm -f "$macos_native_start"
    exit "$status"
  }
  trap 'collect_macos_native_failure "$?"' EXIT
fi

case "${RUNNER_OS:?}" in
  Linux)
    "$exe" "${common[@]}" -class Dsh.Tests.SandboxProcessTests \
      -method 'Dsh.Tests.SandboxProcessTests.Linux*' \
      -method Dsh.Tests.SandboxProcessTests.NativeStdio_RoundTripsWithoutProtocolPollution
    "$exe" "${common[@]}" -class Dsh.Tests.SandboxPinnedInputTests \
      -method 'Dsh.Tests.SandboxPinnedInputTests.Unix*' \
      -method 'Dsh.Tests.SandboxPinnedInputTests.Linux*'
    "$exe" "${common[@]}" -class Dsh.Tests.ScriptPermissionBoundaryTests \
      -class Dsh.Tests.ExternalFrontendSettingsTests -trait 'Category=SandboxNative'
    dotnet publish tests/Dsh.NativePluginSample/Dsh.NativePluginSample.csproj \
      -c Release --nologo -m:1 -nr:false -p:UseSharedCompilation=false
    "$exe" "${common[@]}" -class Dsh.Tests.NativePluginAbiTests -class Dsh.Tests.NativeGuardTests \
      -class Dsh.Tests.NativePluginBridgeTests -class Dsh.Tests.NativeHostCallBufferTests
    ;;
  Windows)
    # No loopback exemption or capability is provisioned: LPAC and job behavior is real.
    "$exe" "${common[@]}" -class Dsh.Tests.SandboxProcessTests \
      -method 'Dsh.Tests.SandboxProcessTests.Windows*'
    "$exe" "${common[@]}" -class Dsh.Tests.WindowsSandboxAuthorityTests
    ;;
  macOS)
    test -x /usr/bin/sandbox-exec
    test -x /usr/bin/perl
    test -x /usr/bin/nc
    # Seatbelt singleton workers only; this does not claim a hard memory or tree limit.
    "$exe" "${common[@]}" -class Dsh.Tests.SandboxProcessTests \
      -method 'Dsh.Tests.SandboxProcessTests.Mac*' \
      -method Dsh.Tests.SandboxProcessTests.NativeStdio_RoundTripsWithoutProtocolPollution
    "$exe" "${common[@]}" -class Dsh.Tests.SandboxPinnedInputTests \
      -method 'Dsh.Tests.SandboxPinnedInputTests.Unix*'
    "$exe" "${common[@]}" \
      -method Dsh.Tests.ExternalProcessBoundaryTests.MacEnabled_OpaqueChildExecutionFailsBeforeMutation \
      -method Dsh.Tests.Mcp.McpSettingsBoundaryTests.MacEnabled_OwnedStdioRefusesUnsupportedChildExecution
    ;;
  *) printf '%s\n' 'Unsupported sandbox CI platform.' >&2; exit 1 ;;
esac

if [ "$RUNNER_OS" != macOS ]; then
  "$exe" "${common[@]}" -trait 'Category=SandboxNative' \
    -class Dsh.Tests.ExternalProcessBoundaryTests -class Dsh.Tests.Mcp.McpSettingsBoundaryTests \
    -method- '*.MacEnabled_*'
fi
"$exe" "${common[@]}" -class Dsh.Tests.PluginSandboxIntegrationTests
"$exe" "${common[@]}" \
  -method 'Dsh.Tests.ExternalProcessBoundaryTests.DefaultOff*' \
  -method Dsh.Tests.ExternalProcessBoundaryTests.RealShell_PreservesInputUnicodeEnvironmentExitAndSpill \
  -method 'Dsh.Tests.Mcp.McpSettingsBoundaryTests.DefaultOff*'
"$exe" "${common[@]}" -class Dsh.Tests.VerifiedPluginLoaderTests
"$exe" "${common[@]}" -class Dsh.Tests.PinnedFileAccessTests
