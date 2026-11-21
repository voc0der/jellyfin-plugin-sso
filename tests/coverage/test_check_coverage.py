"""Checks that CI cannot pass on missing evidence or a rounded-up percentage."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "check-coverage.py"


class CoverageGateTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.results = Path(self.directory.name)

    def write_report(self, covered=100, total=100, name="run"):
        report = self.results / name / "coverage.cobertura.xml"
        report.parent.mkdir()
        report.write_text(
            f'<coverage lines-covered="{covered}" lines-valid="{total}" '
            'branches-covered="9" branches-valid="10" line-rate="1" />',
            encoding="utf-8",
        )
        return report

    def run_gate(self):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(self.results)],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_exact_threshold_passes(self):
        self.write_report()
        result = self.run_gate()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("100/100 (100.00%)", result.stdout)
        self.assertIn("9/10 (90.00%)", result.stdout)

    def test_one_uncovered_line_fails(self):
        self.write_report(1388, 1389)
        self.assertNotEqual(0, self.run_gate().returncode)

    def test_below_threshold_fails_even_if_display_would_round_up(self):
        self.write_report(99999, 100000)
        result = self.run_gate()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("below the required 100%", result.stderr)

    def test_no_report_fails(self):
        self.assertNotEqual(0, self.run_gate().returncode)

    def test_multiple_reports_fail_instead_of_using_stale_results(self):
        self.write_report(name="old")
        self.write_report(name="new")
        result = self.run_gate()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("found 2", result.stderr)

    def test_invalid_counts_fail(self):
        for covered, total in ((0, 0), (-1, 100), (101, 100)):
            with self.subTest(covered=covered, total=total):
                report = self.write_report(covered, total)
                self.assertNotEqual(0, self.run_gate().returncode)
                report.unlink()
                report.parent.rmdir()

    def test_malformed_xml_and_missing_counts_fail(self):
        report = self.write_report()
        for xml in ("broken", "<coverage />"):
            with self.subTest(xml=xml):
                report.write_text(xml, encoding="utf-8")
                self.assertNotEqual(0, self.run_gate().returncode)


if __name__ == "__main__":
    unittest.main()
