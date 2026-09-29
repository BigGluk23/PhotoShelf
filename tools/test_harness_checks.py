"""Exercise gate failures using synthetic reports and a disposable Git repository."""
import contextlib
import importlib.util
import io
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

spec = importlib.util.spec_from_file_location("harness_checks", Path(__file__).with_name("harness_checks.py"))
checks = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checks)


class RepositoryGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="photoshelf-gate-fixture-")
        self.root = Path(self.temp.name).resolve()
        self.real_root = checks.ROOT
        checks.ROOT = self.root
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        files = {
            "Directory.Build.props": "<Project><PropertyGroup><Version>1.2.3-ultra</Version><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            "global.json": '{"sdk":{"version":"10.0.100"}}',
            "README.md": "# PhotoShelf Ultra v1.2.3\n",
            "src/PhotoShelf.Desktop/RUNNING.txt": "PhotoShelf Ultra — Windows x64\n",
            "src/PhotoShelf.Desktop/MainWindow.xaml.cs": 'private const string VersionLabel = "Ultra v1.2.3";',
            "src/PhotoShelf.Desktop/MainWindow.xaml": '<Window xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><TextBlock x:Name="AppTitleText" Text="PhotoShelf Ultra v1.2.3"/></Window>',
        }
        for name in ("PhotoShelf.sln", "AGENTS.md", "HANDOFF.md", "scripts/test-startup-package.ps1", "src/PhotoShelf.Application/Files/SAFETY.md",
                     "src/PhotoShelf.Desktop/Assets/PhotoShelf.ico", "src/PhotoShelf.Desktop/Assets/giraffe-icon.png"):
            files[name] = "fixture"
        for part in checks.PROJECTS:
            project = '<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>' if part == "Desktop" else '<Project/>'
            files[f"src/PhotoShelf.{part}/PhotoShelf.{part}.csproj"] = project
            files[f"tests/PhotoShelf.{part}.Tests/PhotoShelf.{part}.Tests.csproj"] = project
        for name, content in files.items():
            self.write(name, content)

    def tearDown(self):
        checks.ROOT = self.real_root
        self.temp.cleanup()

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def validate(self):
        with contextlib.redirect_stdout(io.StringIO()):
            checks.repository()

    def test_clean_source_passes_and_ignored_build_output_is_allowed(self):
        self.write(".gitignore", (self.real_root / ".gitignore").read_text(encoding="utf-8"))
        self.write("src/PhotoShelf.Desktop/bin/Release/app.dll", "generated")
        self.write("old-release.zip", "generated")
        self.write("src/PhotoShelf.Infrastructure.Sqlite/NewStore.cs", "class NewStore {}")
        self.assertIn("src/PhotoShelf.Infrastructure.Sqlite/NewStore.cs", checks.git("ls-files", "--others", "--exclude-standard"))
        self.validate()

    def test_untracked_database_candidate_is_rejected(self):
        self.write("private/catalog.db", "synthetic")
        with self.assertRaisesRegex(ValueError, "Generated/private"):
            self.validate()

    def test_force_staged_archive_is_rejected_even_when_ignored(self):
        self.write(".gitignore", "*.zip\n")
        self.write("release.zip", "synthetic")
        subprocess.run(["git", "-C", str(self.root), "add", "-f", "release.zip"], check=True)
        with self.assertRaisesRegex(ValueError, "Generated/private"):
            self.validate()

    def test_conflict_and_version_drift_are_rejected(self):
        self.write("conflict.cs", "<<<<<<< local\nclass A {}\n=======\nclass B {}\n>>>>>>> remote\n")
        with self.assertRaisesRegex(ValueError, "conflict marker"):
            self.validate()
        (self.root / "conflict.cs").unlink()
        self.write("README.md", "# PhotoShelf Ultra v1.2.2\n")
        with self.assertRaisesRegex(ValueError, "README heading"):
            self.validate()

    def test_release_instructions_cannot_keep_an_old_version(self):
        self.write("src/PhotoShelf.Desktop/RUNNING.txt", "PhotoShelf Ultra v0.10.6 — Windows x64\n")
        with self.assertRaisesRegex(ValueError, "RUNNING source heading"):
            self.validate()


class ReportGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="photoshelf-trx-fixture-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def report(self, outcome="Passed", reason="", total=2):
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        results = ET.SubElement(root, "Results")
        ET.SubElement(results, "UnitTestResult", outcome="Passed", testName="AlwaysPasses")
        result = ET.SubElement(results, "UnitTestResult", outcome=outcome, testName="SyntheticTest")
        message = ET.SubElement(ET.SubElement(ET.SubElement(result, "Output"), "ErrorInfo"), "Message")
        message.text = reason
        summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
        ET.SubElement(summary, "Counters", total=str(total))
        ET.ElementTree(root).write(self.root / "Domain.trx", encoding="utf-8", xml_declaration=True)

    def validate(self, portable=False, projects=("Domain",)):
        with contextlib.redirect_stdout(io.StringIO()):
            checks.trx(self.root, projects, portable, None)

    def test_passing_report_is_accepted(self):
        self.report()
        self.validate()

    def test_expected_platform_skip_is_visible_but_only_allowed_in_portable_gate(self):
        self.report("NotExecuted", "Requires Windows: synthetic native test")
        self.validate(portable=True)
        with self.assertRaisesRegex(ValueError, "Unexpected skipped"):
            self.validate()

    def test_unexpected_skip_is_rejected(self):
        self.report("NotExecuted", "Temporarily broken")
        with self.assertRaisesRegex(ValueError, "Unexpected skipped"):
            self.validate(portable=True)

    def test_failed_test_is_rejected_even_with_completed_summary(self):
        self.report("Failed")
        with self.assertRaisesRegex(ValueError, "SyntheticTest: Failed"):
            self.validate()

    def test_incomplete_or_missing_project_report_is_rejected(self):
        self.report(total=3)
        with self.assertRaisesRegex(ValueError, "incomplete"):
            self.validate()
        self.report()
        with self.assertRaisesRegex(ValueError, "Expected TRX"):
            self.validate(projects=("Domain", "Application"))


if __name__ == "__main__":
    unittest.main()
