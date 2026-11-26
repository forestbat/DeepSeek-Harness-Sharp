"""Consumer-visible coverage for the dependency-free CI selector."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "select-ci.py"
SPEC = importlib.util.spec_from_file_location("select_ci", SCRIPT)
SELECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SELECTOR)
ALL_SUITES = ["quick", "regression", "raw"]
NATIVE_SUITES = ["quick", "regression", "raw"]


class SelectionTests(unittest.TestCase):
    def assert_selection(self, paths, *, suites=(), jit=False, android=None, aot=False,
                         validate=False, event="pull_request", manual_aot=False):
        # The APK bundles the same shared assemblies as the desktop entries, so it
        # follows jit wherever the selection is conservative; pass android explicitly
        # only to pin that down.
        self.assertEqual(
            SELECTOR.classify_changes(paths, event, manual_aot),
            {"tests": bool(suites), "jit": jit, "android": jit if android is None else android,
             "aot": aot, "validate": validate, "suites": list(suites)},
        )

    def test_safe_documentation_allowlist(self):
        self.assert_selection([
            "README.md", "CONTRIBUTING.md", "docs/guide/nested.txt", "plans/ci.md",
            ".omo/state.json", "LICENSE", "LICENSE.MIT",
            ".github/ISSUE_TEMPLATE/bug.yml", ".github/PULL_REQUEST_TEMPLATE.md",
        ])

    def test_embedded_markdown_is_not_documentation(self):
        for path in ("src/Dsh.Core/prompts/system.md", "assets/README.md", ".github/README.md"):
            with self.subTest(path=path):
                self.assert_selection([path], suites=["quick"], jit=True, aot=True)

    def test_empty_diff_runs_nothing(self):
        self.assert_selection([])

    def test_source_change_covers_quick_and_publishing_only(self):
        # regression 的类全部包含在 quick 里, 源码改动不需要第二份同样的运行。
        for path in ("src/Dsh.Core/Agent.cs", "src/Dsh.Gui/ViewModels/MainViewModel.cs"):
            with self.subTest(path=path):
                self.assert_selection([path], suites=["quick"], jit=True, aot=True)

    def test_native_and_terminal_sources_cover_every_suite(self):
        for path in ("native/plugin_guard/guard.c", "src/Dsh.Tui/ChatWindow.cs",
                     "src/Dsh.Pty/PtyDaemon.cs", "src/Dsh.PtyTerminal/PtyTerminalBackend.cs"):
            with self.subTest(path=path):
                self.assert_selection([path], suites=NATIVE_SUITES, jit=True, aot=True)

    def test_scripts_cover_quick_and_publishing(self):
        for path in ("scripts/smoke-web-host.ps1", "scripts/fetch-iroh-ssh.sh"):
            with self.subTest(path=path):
                self.assert_selection([path], suites=["quick"], jit=True, aot=True)

    def test_test_project_changes_cover_quick_and_regression_without_publishing(self):
        self.assert_selection(
            ["tests/Dsh.Tests/Fixtures/prompt.md", "tests/Dsh.Tests/UnitTests.cs"],
            suites=["quick", "regression"],
        )

    def test_raw_mode_test_changes_add_the_raw_suite(self):
        self.assert_selection(
            ["tests/Dsh.Tests/TuiRawModeEndToEndTests.cs"],
            suites=["quick", "regression", "raw"],
        )

    def test_build_metadata_is_always_conservative(self):
        for path in (
            "tests/Dsh.Tests/Dsh.Tests.csproj", "tests/Dsh.Tests/Directory.Build.props",
            "tests/Dsh.Tests/custom.targets", "Directory.Build.props", "global.json",
            "DeepSeekHarness.slnx", "NuGet.Config",
        ):
            with self.subTest(path=path):
                self.assert_selection([path], suites=ALL_SUITES, jit=True, aot=True)

    def test_build_metadata_is_not_hidden_by_documentation_paths(self):
        for path in ("docs/Directory.Build.props", "plans/Fixture.csproj", ".omo/build.targets"):
            with self.subTest(path=path):
                self.assert_selection([path], suites=ALL_SUITES, jit=True, aot=True)

    def test_other_test_fixture_projects_are_not_test_only(self):
        for path in (
            "tests/Dsh.ManagedPluginSample/Plugin.cs",
            "tests/Dsh.NativePluginSample/SamplePlugin.cs",
            "tests/Dsh.IocAotSmoke/Program.cs",
        ):
            with self.subTest(path=path):
                self.assert_selection([path], suites=["quick"], jit=True, aot=True)

    def test_mixed_paths_take_the_union(self):
        self.assert_selection(
            ["docs/ci.md", "tests/Dsh.Tests/TuiRawModeEndToEndTests.cs",
             ".github/workflows/release.yml"],
            suites=["quick", "regression", "raw"], validate=True,
        )
        self.assert_selection(
            ["README.md", "tests/Dsh.Tests/UnitTests.cs", "src/Dsh.Core/Agent.cs"],
            suites=["quick", "regression"], jit=True, aot=True,
        )

    def test_workflow_changes_only_require_validation(self):
        # CI 自身的改动由 actionlint 与选测规则单测把关, 不必为此再跑一遍全部测试。
        for path in (
            ".github/workflows/release.yml", ".github/workflows/interactive.yml",
            ".github/workflows/ci.yml", ".github/workflows/ci-tests.yml",
            ".github/workflows/ci-aot.yml", ".github/workflows/new-job.yaml",
        ):
            with self.subTest(path=path):
                self.assert_selection([path], validate=True)

    def test_selector_changes_only_require_validation(self):
        for path in ("scripts/select-ci.py", "tests/ci/test_select_ci.py", "tests/ci/test_other.py"):
            with self.subTest(path=path):
                self.assert_selection([path], validate=True)

    def test_unknown_and_deleted_paths_do_not_require_file_existence(self):
        for path in ("unexpected/path", "src/Deleted.cs", "tests/Dsh.Tests/DeletedTests.cs"):
            with self.subTest(path=path):
                if path == "tests/Dsh.Tests/DeletedTests.cs":
                    self.assert_selection([path], suites=["quick", "regression"])
                else:
                    self.assert_selection([path], suites=["quick"], jit=True, aot=True)

    def test_android_apk_is_built_only_for_its_own_or_shared_changes(self):
        # Shared code also rebuilds the APK, which bundles the same assemblies.
        self.assert_selection(["src/Dsh.Core/Agent.cs"], suites=["quick"], jit=True, android=True, aot=True)
        # Build metadata is conservative everywhere, APK included.
        self.assert_selection(["Directory.Build.props"], suites=ALL_SUITES, jit=True, android=True, aot=True)
        # An Android-only change rebuilds the APK and nothing on the desktop.
        self.assert_selection(["src/Dsh.Android/MainActivity.cs"], suites=[], jit=False, android=True)
        self.assert_selection(["README.md", "docs/ci.md"], android=False)
        self.assert_selection(["tests/Dsh.Tests/UnitTests.cs"], suites=["quick", "regression"], android=False)

    def test_push_does_not_add_aot(self):
        self.assert_selection(["src/Dsh.Core/Agent.cs"], event="push", suites=["quick"], jit=True)

    def test_manual_runs_everything_except_opted_out_aot(self):
        for aot in (False, True):
            with self.subTest(aot=aot):
                self.assert_selection([], event="workflow_dispatch", manual_aot=aot,
                                      suites=ALL_SUITES, jit=True, aot=aot, validate=True)


class CommandLineTests(unittest.TestCase):
    def run_selector(self, *, event="push", base="HEAD", head="HEAD", aot="false",
                     output=None, cwd=ROOT):
        command = [sys.executable, str(SCRIPT), "--event", event, "--base", base,
                   "--head", head, "--aot", aot]
        if output is not None:
            command.extend(["--output", str(output)])
        return subprocess.run(command, cwd=cwd, capture_output=True, text=True)

    def parse_output(self, output):
        return {
            key: json.loads(value)
            for key, value in (line.split("=", 1) for line in output.splitlines())
        }

    def test_initial_push_does_not_need_diffable_revisions(self):
        for base in ("", "0" * 40):
            with self.subTest(base=base):
                result = self.run_selector(base=base, head="invalid-revision", aot="true")
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parse_output(result.stdout), {
                    "tests": True, "jit": True, "android": True, "aot": False, "validate": True,
                    "suites": ALL_SUITES,
                })

    def test_manual_bypasses_git_and_honors_aot_input(self):
        for aot in ("true", "false"):
            with self.subTest(aot=aot):
                result = self.run_selector(event="workflow_dispatch", base="invalid-base",
                                           head="invalid-head", aot=aot)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.parse_output(result.stdout), {
                    "tests": True, "jit": True, "android": True, "aot": aot == "true", "validate": True,
                    "suites": ALL_SUITES,
                })

    def test_invalid_git_revisions_fail_without_selection_output(self):
        for event, base, head in (
            ("push", "invalid-base:revision", "HEAD"),
            ("push", "HEAD", "invalid-head:revision"),
            ("pull_request", "0" * 40, "HEAD"),
            ("pull_request", "", "HEAD"),
        ):
            with self.subTest(event=event, base=base, head=head):
                result = self.run_selector(event=event, base=base, head=head)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")

    def test_output_file_is_appended_and_empty_diff_runs_nothing(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            output = Path(directory) / "github-output"
            output.write_text("existing=value\n", encoding="utf-8")
            result = self.run_selector(output=output)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, "")
            contents = output.read_text(encoding="utf-8")
            self.assertTrue(contents.startswith("existing=value\n"))
            self.assertEqual(self.parse_output(contents.removeprefix("existing=value\n")), {
                "tests": False, "jit": False, "android": False, "aot": False, "validate": False, "suites": [],
            })

    def test_real_git_diff_preserves_newlines_in_paths(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.run(["git", *args], cwd=root, check=True,
                                      capture_output=True, text=True).stdout.strip()
            git("init")
            git("-c", "user.name=CI Selector", "-c", "user.email=selector@example.invalid",
                "commit", "--allow-empty", "-m", "Base")
            base = git("rev-parse", "HEAD")
            source = root / "src" / "embedded\nprompt.md"
            source.parent.mkdir()
            source.write_text("A production prompt", encoding="utf-8")
            git("add", "--", "src")
            git("-c", "user.name=CI Selector", "-c", "user.email=selector@example.invalid",
                "commit", "-m", "Prompt")
            result = self.run_selector(event="pull_request", base=base, head="HEAD", cwd=root)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(self.parse_output(result.stdout), {
                "tests": True, "jit": True, "android": True, "aot": True, "validate": False,
                "suites": ["quick"],
            })

    def test_rename_from_production_to_documentation_keeps_production_checks(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.run(["git", *args], cwd=root, check=True,
                                      capture_output=True, text=True).stdout.strip()
            git("init")
            source = root / "src" / "prompt.md"
            source.parent.mkdir()
            source.write_text("A production prompt", encoding="utf-8")
            git("add", "--", "src")
            git("-c", "user.name=CI Selector", "-c", "user.email=selector@example.invalid",
                "commit", "-m", "Production prompt")
            base = git("rev-parse", "HEAD")
            (root / "docs").mkdir()
            git("mv", "src/prompt.md", "docs/prompt.md")
            git("-c", "user.name=CI Selector", "-c", "user.email=selector@example.invalid",
                "commit", "-m", "Move prompt")
            result = self.run_selector(event="pull_request", base=base, head="HEAD", cwd=root)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(self.parse_output(result.stdout), {
                "tests": True, "jit": True, "android": True, "aot": True, "validate": False,
                "suites": ["quick"],
            })


if __name__ == "__main__":
    unittest.main()
