"""Isolated behavior checks for the read-only project auditor."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "audit_project.py"
SPEC = importlib.util.spec_from_file_location("mirror_audit_project", SCRIPT)
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)


class AuditProjectTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.base = Path(self.temporary.name).resolve()
        self.project = self.base / "Game"
        for name in ("Assets", "Packages", "ProjectSettings"):
            (self.project / name).mkdir(parents=True)
        self.write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.3.6f1\n")
        self.write_json("Packages/manifest.json", {"dependencies": {}})
        self.write_json("Packages/packages-lock.json", {"dependencies": {}})

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, relative, text):
        destination = self.project / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8")
        return destination

    def write_json(self, relative, value):
        return self.write(relative, json.dumps(value))

    def cli(self, *arguments):
        return subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--project", str(self.project), *arguments],
            capture_output=True, text=True, check=False,
        )

    def directory_link(self, link, target):
        try:
            link.symlink_to(target, target_is_directory=True)
            return
        except OSError:
            if os.name == "nt":
                created = subprocess.run(
                    ["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(target)],
                    capture_output=True, check=False,
                )
                if created.returncode == 0:
                    return
        self.skipTest("This environment cannot create directory links or junctions.")

    def test_valid_minimal_project_has_no_pipeline(self):
        report = AUDIT.audit_project(self.project)
        self.assertTrue(report["valid"])
        self.assertEqual(report["unity_version"], "6000.3.6f1")
        self.assertFalse(report["pipeline"]["required"])
        self.assertFalse(report["pipeline"]["descriptor_present"])
        self.assertFalse(report["mirror"]["detected"])

    def test_exact_pipeline_directory_requires_testing_without_manifest_entry(self):
        (self.project / "Packages" / "com.unity.pipeline").mkdir()
        report = AUDIT.audit_project(self.project)
        self.assertTrue(report["pipeline"]["required"])
        self.assertEqual(
            report["pipeline"]["embedded_path"],
            str(self.project / "Packages" / "com.unity.pipeline"),
        )
        self.assertIsNone(report["pipeline"]["package_version"])

    def test_similar_or_nested_pipeline_names_do_not_trigger(self):
        for name in (
            "Packages/com.unity.pipeline-extra",
            "Assets/Packages/com.unity.pipeline",
            "Packages/custom/com.unity.pipeline",
        ):
            (self.project / name).mkdir(parents=True)
        self.assertFalse(AUDIT.audit_project(self.project)["pipeline"]["required"])

    def test_manifest_and_lock_declarations_each_trigger_pipeline(self):
        self.write_json(
            "Packages/manifest.json",
            {"dependencies": {"com.unity.pipeline": "0.3.1-exp.1"}},
        )
        manifest = AUDIT.audit_project(self.project)
        self.assertTrue(manifest["pipeline"]["required"])
        self.assertEqual(manifest["pipeline"]["package_version"], "0.3.1-exp.1")
        self.write_json("Packages/manifest.json", {"dependencies": {}})
        self.write_json(
            "Packages/packages-lock.json",
            {"dependencies": {
                "com.unity.pipeline": {
                    "version": "0.3.2", "depth": 1, "source": "registry",
                    "dependencies": {},
                },
            }},
        )
        locked = AUDIT.audit_project(self.project)
        self.assertTrue(locked["pipeline"]["required"])
        self.assertEqual(locked["pipeline"]["package_version"], "0.3.2")

    def test_invalid_pipeline_declaration_still_requires_testing(self):
        self.write_json(
            "Packages/manifest.json", {"dependencies": {"com.unity.pipeline": None}}
        )
        report = AUDIT.audit_project(self.project)
        self.assertFalse(report["valid"])
        self.assertTrue(report["pipeline"]["required"])

    def test_pipeline_metadata_and_docs_never_open_descriptor(self):
        self.write_json(
            "Packages/com.unity.pipeline/package.json",
            {"name": "com.unity.pipeline", "version": "0.3.1-exp.1"},
        )
        documentation = self.write(
            "Packages/com.unity.pipeline/Documentation~/connectivity.md", "Local docs"
        )
        descriptor = self.write_json(
            "Library/Pipeline/.unity-pipeline-port",
            {"evalToken": "DO_NOT_LEAK_DESCRIPTOR_TOKEN", "port": 7800},
        )
        original_read = Path.read_text

        def guard(path, *args, **kwargs):
            if path == descriptor:
                self.fail("The instance descriptor must never be opened.")
            return original_read(path, *args, **kwargs)

        with patch.object(Path, "read_text", guard):
            report = AUDIT.audit_project(self.project)
        encoded = json.dumps(report)
        self.assertTrue(report["pipeline"]["descriptor_present"])
        self.assertEqual(report["pipeline"]["descriptor_path"], str(descriptor))
        self.assertEqual(report["pipeline"]["package_version"], "0.3.1-exp.1")
        self.assertIn(str(documentation), report["pipeline"]["docs"])
        self.assertNotIn("DO_NOT_LEAK_DESCRIPTOR_TOKEN", encoded)
        self.assertNotIn("evalToken", encoded)

    def test_malformed_and_missing_project_metadata_fail_with_json(self):
        for relative in ("Packages/manifest.json", "Packages/packages-lock.json"):
            with self.subTest(relative=relative, mode="malformed"):
                target = self.project / relative
                before = target.read_text()
                target.write_text("{invalid", encoding="utf-8")
                result = self.cli()
                self.assertNotEqual(result.returncode, 0)
                report = json.loads(result.stdout)
                self.assertFalse(report["valid"])
                self.assertIn("invalid_json", {item["code"] for item in report["errors"]})
                target.write_text(before, encoding="utf-8")
            with self.subTest(relative=relative, mode="missing"):
                target = self.project / relative
                before = target.read_text()
                target.unlink()
                report = AUDIT.audit_project(self.project)
                self.assertFalse(report["valid"])
                self.assertIn("missing_file", {item["code"] for item in report["errors"]})
                target.write_text(before, encoding="utf-8")
        missing = AUDIT.audit_project(self.base / "Missing")
        self.assertFalse(missing["valid"])
        self.assertEqual(missing["errors"][0]["code"], "missing_project")
        relative = AUDIT.audit_project("relative/project")
        self.assertEqual(relative["errors"][0]["code"], "absolute_path_required")

    def test_dependency_shapes_and_unity_version_are_validated(self):
        self.write_json("Packages/manifest.json", {"dependencies": []})
        self.write_json(
            "Packages/packages-lock.json",
            {"dependencies": {"com.example.package": {"version": 1}}},
        )
        self.write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: invalid!\n")
        report = AUDIT.audit_project(self.project)
        self.assertFalse(report["valid"])
        self.assertEqual(
            {item["code"] for item in report["errors"]},
            {"invalid_dependencies", "invalid_dependency", "invalid_unity_version"},
        )

    def test_malformed_optional_package_name_does_not_crash_audit(self):
        self.write_json(
            "Packages/Unrecognized/package.json", {"name": ["invalid"], "version": {}}
        )
        report = AUDIT.audit_project(self.project)
        self.assertTrue(report["valid"])
        self.assertFalse(report["mirror"]["detected"])

    def test_hardlinked_descriptor_alias_is_never_read(self):
        descriptor = self.write_json(
            "Library/Pipeline/.unity-pipeline-port",
            {"evalToken": "DO_NOT_LEAK_HARDLINK_TOKEN"},
        )
        alias = self.project / "Packages" / "com.unity.pipeline" / "package.json"
        alias.parent.mkdir()
        try:
            os.link(descriptor, alias)
        except OSError:
            self.skipTest("This environment cannot create hard links.")
        report = AUDIT.audit_project(self.project)
        self.assertTrue(report["pipeline"]["required"])
        self.assertNotIn("DO_NOT_LEAK_HARDLINK_TOKEN", json.dumps(report))
        self.assertIn(
            "protected_descriptor", {item["code"] for item in report["warnings"]}
        )

    def test_deterministic_inventories_and_mirror_asset_detection(self):
        self.write("Assets/ZScene.unity", "%YAML 1.1")
        self.write("Assets/Scenes/AScene.unity", "%YAML 1.1")
        self.write("Assets/Objects/Player.prefab", "%YAML 1.1")
        self.write_json("Assets/Mirror/Core/Mirror.asmdef", {"name": "Mirror"})
        self.write_json(
            "Assets/Tests/Gameplay.asmdef",
            {"name": "GameplayTests", "optionalUnityReferences": ["TestAssemblies"]},
        )
        self.write_json(
            "Assets/Gameplay.asmdef", {"name": "Gameplay", "references": ["Mirror"]}
        )
        for excluded in ("Library", "Temp", "obj", "Build", "Logs", ".git"):
            self.write(excluded + "/Ignored.unity", "ignored")
            self.write("Assets/" + excluded + "/Ignored.prefab", "ignored")
        report = AUDIT.audit_project(self.project)
        self.assertEqual(report, AUDIT.audit_project(self.project))
        self.assertEqual(report["scenes"], ["Assets/Scenes/AScene.unity", "Assets/ZScene.unity"])
        self.assertEqual(report["prefabs"], ["Assets/Objects/Player.prefab"])
        self.assertEqual(report["test_asmdefs"], ["Assets/Tests/Gameplay.asmdef"])
        self.assertTrue(report["mirror"]["detected"])
        self.assertEqual(report["mirror"]["roots"][0]["path"], "Assets/Mirror")

    def test_package_dependencies_are_structured_and_credentials_redacted(self):
        self.write_json(
            "Packages/manifest.json",
            {"dependencies": {
                "com.vis2k.mirror":
                    "https://user:DO_NOT_LEAK_PASSWORD@example.test/Mirror.git"
                    "?token=DO_NOT_LEAK_QUERY#v99.0.0",
                "com.example.package": "1.2.3",
            }},
        )
        self.write_json(
            "Packages/packages-lock.json",
            {"dependencies": {
                "com.vis2k.mirror": {
                    "version": "99.0.0", "source": "git", "depth": 0,
                    "dependencies": {},
                },
            }},
        )
        report = AUDIT.audit_project(self.project)
        self.assertTrue(report["valid"])
        self.assertEqual(
            [item["name"] for item in report["dependencies"]],
            ["com.example.package", "com.vis2k.mirror"],
        )
        self.assertEqual(
            report["dependencies"][1]["requested"],
            "https://example.test/Mirror.git#v99.0.0",
        )
        self.assertEqual(report["mirror"]["dependencies"], ["com.vis2k.mirror"])
        self.assertNotIn("DO_NOT_LEAK", json.dumps(report))

    def test_external_links_are_not_traversed(self):
        outside = self.base / "Outside"
        outside.mkdir()
        (outside / "External.unity").write_text("external", encoding="utf-8")
        link = self.project / "Assets" / "Linked"
        self.directory_link(link, outside)
        report = AUDIT.audit_project(self.project)
        self.assertEqual(report["scenes"], [])
        self.directory_link(
            self.project / "Packages" / "com.unity.pipeline", outside
        )
        linked_pipeline = AUDIT.audit_project(self.project)
        self.assertTrue(linked_pipeline["pipeline"]["required"])
        self.assertEqual(linked_pipeline["pipeline"]["docs"], [])

    def test_cli_safe_output_and_no_project_changes(self):
        before = {
            path.relative_to(self.project).as_posix(): path.read_bytes()
            for path in self.project.rglob("*") if path.is_file()
        }
        output = self.base / "audit.json"
        result = self.cli("--output", str(output))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(output.read_text()), json.loads(result.stdout))
        after = {
            path.relative_to(self.project).as_posix(): path.read_bytes()
            for path in self.project.rglob("*") if path.is_file()
        }
        self.assertEqual(before, after)
        for unsafe in (self.project / "audit.json", self.base / "audit.txt"):
            with self.subTest(output=unsafe):
                rejected = self.cli("--output", str(unsafe))
                self.assertNotEqual(rejected.returncode, 0)
                self.assertFalse(json.loads(rejected.stdout)["valid"])
                self.assertFalse(unsafe.exists())

    def test_cli_rejects_output_via_project_symlink_and_preserves_existing_file(self):
        existing = self.write("Assets/existing.json", '{"preserve": true}')
        result = self.cli("--output", str(existing))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(existing.read_text(), '{"preserve": true}')
        alias = self.base / "ProjectAlias"
        self.directory_link(alias, self.project)
        result = self.cli("--output", str(alias / "report.json"))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.project / "report.json").exists())


if __name__ == "__main__":
    unittest.main()
