"""Exercise synchronization failure handling with isolated fake git/dotnet commands."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "update-core.sh"


class UpdateCoreTests(unittest.TestCase):
    def run_sync(self, *, test_failure=False, drift_failure=False, args=()):
        with tempfile.TemporaryDirectory(prefix="core-sync-check-") as temp:
            root = Path(temp)
            commands = root / "commands"
            commands.mkdir()
            (commands / "git").write_text('''#!/bin/bash
case "$1" in
  rev-parse) [[ "$*" == *--is-inside-work-tree* ]] && echo true || echo 0123456789abcdef ;;
  remote) echo https://example.invalid/core ;;
  log) echo '01234567 core change' ;;
  *) : ;;
esac
''')
            (commands / "dotnet").write_text('''#!/bin/bash
[[ "$1" == --version ]] && { echo 10.0.114; exit 0; }
if [[ "$1" == test && "$FAKE_TEST_FAILURE" == 1 ]]; then exit 17; fi
exit 0
''')
            for command in commands.iterdir():
                command.chmod(0o755)
            for project in ["SmartFactory.MES.csproj", "SmartFactory.MES.Tests/SmartFactory.MES.Tests.csproj",
                            "SmartFactory.MES.IntegrationTests/SmartFactory.MES.IntegrationTests.csproj",
                            "SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj"]:
                path = root / project
                path.parent.mkdir(exist_ok=True)
                path.touch()
            scripts = root / "scripts"
            scripts.mkdir()
            for name in ["check-snapshot-drift.sh", "check-core-drift.sh", "check-ours-drops.sh"]:
                path = scripts / name
                code = 23 if drift_failure and name == "check-core-drift.sh" else 0
                path.write_text(f"#!/bin/bash\nexit {code}\n")
                path.chmod(0o755)
            original = "LAST_SYNC=before\nLAST_SYNC_SHA=before\n"
            marker = root / ".core-version"
            marker.write_text(original)
            result = subprocess.run(["bash", str(SCRIPT), *args], cwd=root,
                env={**os.environ, "PATH": f"{commands}:{os.environ['PATH']}",
                     "FAKE_TEST_FAILURE": "1" if test_failure else "0"},
                capture_output=True, text=True)
            return result, marker.read_text(), original

    def test_failed_tests_never_mark_synchronization_complete(self):
        result, marker, original = self.run_sync(test_failure=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, marker)
        self.assertNotIn("核心更新完成", result.stdout)

    def test_failed_drift_check_never_marks_synchronization_complete(self):
        result, marker, original = self.run_sync(drift_failure=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, marker)
        self.assertNotIn("核心更新完成", result.stdout)

    def test_skip_build_cannot_perform_a_merge(self):
        result, marker, original = self.run_sync(args=("--skip-build",))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, marker)

    def test_success_updates_marker_only_after_checks(self):
        result, marker, original = self.run_sync()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotEqual(original, marker)
        self.assertIn("LAST_SYNC_SHA=0123456789abcdef", marker)


if __name__ == "__main__":
    unittest.main()
