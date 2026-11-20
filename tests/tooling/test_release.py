import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

from scripts import plugin

spec = importlib.util.spec_from_file_location("checks", plugin.ROOT / "scripts/wait-for-checks.py")
checks = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checks)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ("build.yaml", "manifest.json", "LICENSE.txt", "SSO-Auth/SSO-Auth.csproj",
                     "SSO-Auth/Lib/Lib.fsproj", "SSO-Auth/SSOPlugin.cs",
                     "SSO-Auth/Config/config.js", "SSO-Auth/Config/linking.js"):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(plugin.ROOT / name, target)
        manifest = json.loads((self.root / "manifest.json").read_text())
        manifest[0]["versions"] = []
        (self.root / "manifest.json").write_text(json.dumps(manifest))
        self.publish = self.root / "publish"
        self.publish.mkdir()
        for name in plugin.build_metadata(self.root)["artifacts"]:
            (self.publish / name).write_bytes(b"test assembly")

    def test_archive_includes_runtime_dependencies_but_not_server_dlls(self):
        (self.publish / "MediaBrowser.Controller.dll").write_bytes(b"server-owned")
        archive = self.root / "plugin.zip"
        plugin.package(self.publish, archive, "4.0.0.5", "pr/7", self.root)
        with ZipFile(archive) as zipped:
            self.assertEqual(set(zipped.namelist()), {
                f"SSO_Authentication_4.0.0.5/{name}" for name in (
                    "SSO-Auth.dll", "Lib.dll", "FSharp.Core.dll",
                    "Duende.IdentityModel.OidcClient.dll", "Duende.IdentityModel.dll",
                    "LICENSE.txt", "BUILD_TAG.txt",
                )
            })
            self.assertEqual(zipped.read("SSO_Authentication_4.0.0.5/BUILD_TAG.txt"), b"pr/7\n")
        self.assertTrue(archive.with_suffix(".zip.sha256").is_file())

    def test_missing_dependency_cannot_produce_a_release(self):
        (self.publish / "Duende.IdentityModel.dll").unlink()
        archive = self.root / "plugin.zip"
        with self.assertRaisesRegex(ValueError, "Missing runtime dependency"):
            plugin.package(self.publish, archive, "4.0.0.5", root=self.root)
        self.assertFalse(archive.exists())

    def test_manifest_uses_fork_download_and_retains_previous_releases(self):
        version = plugin.next_version(self.root)
        self.assertEqual(version, "4.0.0.5")
        notes = self.root / "notes.md"
        notes.write_text("Release notes\n")
        for current in (version, "4.0.0.6"):
            archive = self.root / f"SSO_Authentication_{current}.zip"
            plugin.package(self.publish, archive, current, root=self.root)
            plugin.update_manifest(current, archive, notes, self.root)
        _, manifest = plugin.validate(self.root)
        self.assertEqual([item["version"] for item in manifest["versions"]], ["4.0.0.6", "4.0.0.5"])
        release = manifest["versions"][0]
        self.assertIn("voc0der/jellyfin-plugin-sso/releases/download/4.0.0.6/", release["sourceUrl"])
        self.assertEqual(release["targetAbi"], "10.11.0.0")
        self.assertEqual(len(release["checksum"]), 32)
        with self.assertRaisesRegex(ValueError, "must follow"):
            plugin.update_manifest("4.0.0.6", archive, notes, self.root)

    def test_rejects_mismatched_identity(self):
        path = self.root / "SSO-Auth/Config/linking.js"
        path.write_text(path.read_text().replace("505ce9d1", "00000000"))
        with self.assertRaisesRegex(ValueError, "GUID is inconsistent"):
            plugin.validate(self.root)

    def test_versions_cannot_escape_archive_or_overflow_assembly_version(self):
        for version in ("../../evil", "nightly", "v4.0.0.4", "4.0.0.65535", "4.0.0.4-extra"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                plugin.version_tuple(version)


class ReleaseChecksTests(unittest.TestCase):
    def runs(self):
        return [dict(name=name, event="push", status="completed", conclusion="success")
                for name in checks.REQUIRED]

    def test_only_successful_complete_push_checks_allow_release(self):
        runs = self.runs()
        self.assertTrue(checks.check_status(runs))
        self.assertFalse(checks.check_status(runs[:-1]))
        runs[0]["event"] = "pull_request"
        self.assertFalse(checks.check_status(runs))

    def test_failed_cancelled_or_skipped_workflows_block_release(self):
        for conclusion in ("failure", "cancelled", "skipped", "timed_out"):
            runs = self.runs()
            runs[0]["conclusion"] = conclusion
            with self.subTest(conclusion=conclusion), self.assertRaises(RuntimeError):
                checks.check_status(runs)

    def test_rerun_must_finish_before_old_success_can_be_used(self):
        runs = self.runs()
        pending = dict(runs[0], status="in_progress", conclusion=None)
        self.assertFalse(checks.check_status([pending] + runs))


if __name__ == "__main__":
    unittest.main()
