"""Select CI consumers from changed paths without requiring third-party packages."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


SUITES = ("quick", "regression-1", "regression-2", "regression-3", "raw")
LIGHT_WORKFLOWS = {
    ".github/workflows/release.yml",
    ".github/workflows/release-assets.yml",
    ".github/workflows/interactive.yml",
}


def is_documentation(path):
    if path.startswith(("docs/", "plans/", ".omo/", ".github/ISSUE_TEMPLATE/")):
        return True
    if path == ".github/PULL_REQUEST_TEMPLATE.md":
        return True
    return "/" not in path and (
        path.endswith(".md") or path == "LICENSE" or path.startswith("LICENSE.")
    )


def classify_changes(paths, event, aot=False, full=False):
    """Return consumer selections; paths may name files that no longer exist."""
    full = full or event == "workflow_dispatch"
    suites = set(SUITES) if full else set()
    jit = full
    validate = full
    for path in paths:
        workflow = path.startswith(".github/workflows/") and path.endswith((".yml", ".yaml"))
        if workflow or path == "scripts/select-ci.py" or path.startswith("tests/ci/"):
            validate = True
        if path.endswith((".csproj", ".props", ".targets")):
            suites.update(SUITES)
            jit = True
        elif is_documentation(path) or path in LIGHT_WORKFLOWS:
            continue
        elif path.startswith("tests/Dsh.Tests/"):
            # This project owns the native raw tests and their shared fixtures.
            suites.update(SUITES)
        else:
            suites.update(SUITES)
            jit = True
    return {
        "tests": bool(suites),
        "jit": jit,
        # The APK bundles the same shared assemblies the desktop entries publish,
        # so it follows the JIT selection instead of keeping its own path rules.
        "android": jit,
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
