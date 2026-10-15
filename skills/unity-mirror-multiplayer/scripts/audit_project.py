#!/usr/bin/env python3
"""Read-only Unity/Mirror inventory; never opens Pipeline instance descriptors."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys
import tempfile
from typing import Any
from urllib.parse import urlsplit, urlunsplit


PIPELINE_PACKAGE = "com.unity.pipeline"
MIRROR_PACKAGES = {
    "com.mirror-networking.mirror",
    "com.mirrornetworking.mirror",
    "com.vis2k.mirror",
}
EXCLUDED_DIRS = {"library", "temp", "obj", "build", "builds", "logs", ".git"}
PACKAGE_NAME = re.compile(r"^[a-z0-9][a-z0-9._-]*$")
VERSION = re.compile(r"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$")
UNITY_VERSION = re.compile(r"^\d+\.\d+\.\d+(?:[A-Za-z][A-Za-z0-9.-]*)?$")


def _error(
    errors: list[dict[str, str]], code: str, path: str, message: str
) -> None:
    errors.append({"code": code, "path": path, "message": message})


def _within(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root)
        return True
    except (OSError, RuntimeError, ValueError):
        return False


def _linked_directory(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
        return path.is_symlink() or bool(
            attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )
    except OSError:
        return True


def _descriptor_alias(path: Path, root: Path) -> bool:
    descriptor = root / "Library" / "Pipeline" / ".unity-pipeline-port"
    try:
        return path.resolve() == descriptor.resolve() or (
            descriptor.is_file() and path.is_file() and path.samefile(descriptor)
        )
    except (OSError, RuntimeError):
        return True


def _read_json(
    path: Path,
    root: Path,
    errors: list[dict[str, str]],
    *,
    required: bool = True,
) -> dict[str, Any] | None:
    relative = path.relative_to(root).as_posix()
    if not _within(path, root):
        _error(errors, "external_path", relative, "Metadata resolves outside the project.")
        return None
    if _descriptor_alias(path, root):
        _error(errors, "protected_descriptor", relative, "Instance descriptors are never read.")
        return None
    if not path.is_file():
        if required:
            _error(errors, "missing_file", relative, "Required metadata file is missing.")
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        _error(errors, "invalid_json", relative, "Cannot read a valid JSON object.")
        return None
    if not isinstance(data, dict):
        _error(errors, "invalid_json_shape", relative, "Expected a JSON object.")
        return None
    return data


def _safe_dependency(value: Any) -> str | None:
    """Preserve useful versions/locations without URL credentials or query tokens."""
    if not isinstance(value, str):
        return None
    if VERSION.fullmatch(value):
        return value
    if value.startswith("file:"):
        return value.split("?", 1)[0].split("#", 1)[0]
    prefix = "git+" if value.startswith("git+") else ""
    candidate = value[len(prefix):]
    try:
        parsed = urlsplit(candidate)
        if parsed.scheme in {"http", "https", "ssh", "git"} and parsed.hostname:
            host = parsed.hostname
            if ":" in host:
                host = "[" + host + "]"
            if parsed.port is not None:
                host += ":" + str(parsed.port)
            # Git fragments identify revisions; only ordinary ref characters survive.
            fragment = parsed.fragment
            if not re.fullmatch(r"[A-Za-z0-9._/-]*", fragment):
                fragment = ""
            return prefix + urlunsplit(
                (parsed.scheme, host, parsed.path, "", fragment)
            )
    except ValueError:
        pass
    if re.fullmatch(r"[^@\s]+@[^:\s]+:[^\s?#]+(?:#[A-Za-z0-9._/-]+)?", value):
        return value.split("@", 1)[1]
    return "<unrecognized>"


def _dependencies(
    manifest: dict[str, Any] | None,
    lock: dict[str, Any] | None,
    errors: list[dict[str, str]],
) -> tuple[list[dict[str, Any]], dict[str, Any], dict[str, Any]]:
    requested = manifest.get("dependencies") if manifest is not None else {}
    resolved = lock.get("dependencies") if lock is not None else {}
    for value, path in (
        (requested, "Packages/manifest.json"),
        (resolved, "Packages/packages-lock.json"),
    ):
        if not isinstance(value, dict):
            _error(
                errors, "invalid_dependencies", path,
                "The dependencies field must be an object.",
            )
    requested = requested if isinstance(requested, dict) else {}
    resolved = resolved if isinstance(resolved, dict) else {}
    valid_requested: dict[str, Any] = {}
    valid_resolved: dict[str, Any] = {}
    for name, value in requested.items():
        if not PACKAGE_NAME.fullmatch(name) or not isinstance(value, str):
            _error(
                errors, "invalid_dependency", "Packages/manifest.json",
                "A dependency has an invalid package name or version value.",
            )
            continue
        valid_requested[name] = value
    for name, value in resolved.items():
        if (
            not PACKAGE_NAME.fullmatch(name)
            or not isinstance(value, dict)
            or not isinstance(value.get("version"), str)
            or (
                "depth" in value
                and (type(value["depth"]) is not int or value["depth"] < 0)
            )
            or ("source" in value and not isinstance(value["source"], str))
            or (
                "dependencies" in value
                and (
                    not isinstance(value["dependencies"], dict)
                    or any(
                        not PACKAGE_NAME.fullmatch(child)
                        or not isinstance(version, str)
                        for child, version in value["dependencies"].items()
                    )
                )
            )
        ):
            _error(
                errors, "invalid_dependency", "Packages/packages-lock.json",
                "A dependency has an invalid lock record.",
            )
            continue
        valid_resolved[name] = value
    records = []
    for name in sorted(valid_requested.keys() | valid_resolved.keys()):
        locked = valid_resolved.get(name, {})
        source = locked.get("source")
        if source not in {
            "registry", "builtin", "embedded", "local", "git", "tarball"
        }:
            source = None
        records.append({
            "name": name,
            "requested": _safe_dependency(valid_requested.get(name)),
            "resolved": _safe_dependency(locked.get("version")),
            "source": source,
            "depth": locked.get("depth"),
        })
    return records, valid_requested, valid_resolved


def _inventory(
    root: Path, warnings: list[dict[str, str]]
) -> list[Path]:
    files: list[Path] = []

    def failed(_: OSError) -> None:
        _error(
            warnings, "inventory_unreadable", "",
            "A project directory could not be inventoried.",
        )

    for base in (root / "Assets", root / "Packages"):
        if not base.is_dir() or not _within(base, root) or _linked_directory(base):
            continue
        for current, directories, names in os.walk(base, followlinks=False, onerror=failed):
            parent = Path(current)
            directories[:] = sorted(
                name for name in directories
                if name.lower() not in EXCLUDED_DIRS
                and not _linked_directory(parent / name)
                and _within(parent / name, root)
            )
            for name in sorted(names):
                path = parent / name
                if path.suffix.lower() in {".unity", ".prefab", ".asmdef", ".json", ".md"}:
                    if path.is_file() and _within(path, root):
                        files.append(path)
    return sorted(files, key=lambda item: item.relative_to(root).as_posix())


def _test_assembly(data: dict[str, Any]) -> bool:
    for field in (
        "optionalUnityReferences", "references", "precompiledReferences",
        "defineConstraints",
    ):
        values = data.get(field, [])
        if isinstance(values, list) and any(
            isinstance(value, str) and value in {
                "TestAssemblies", "UnityEngine.TestRunner", "UnityEditor.TestRunner",
                "nunit.framework", "nunit.framework.dll", "UNITY_INCLUDE_TESTS",
            }
            for value in values
        ):
            return True
    return False


def audit_project(project: str | os.PathLike[str]) -> dict[str, Any]:
    """Return a deterministic JSON-safe inventory without modifying the project."""
    errors: list[dict[str, str]] = []
    warnings: list[dict[str, str]] = []
    given = Path(project).expanduser()
    report: dict[str, Any] = {
        "schema_version": 1,
        "project_root": str(given),
        "valid": False,
        "errors": errors,
        "warnings": warnings,
        "unity_version": None,
        "dependencies": [],
        "mirror": {"detected": False, "roots": [], "dependencies": []},
        "scenes": [],
        "prefabs": [],
        "test_asmdefs": [],
        "pipeline": {
            "required": False,
            "embedded_path": None,
            "package_version": None,
            "descriptor_path": None,
            "descriptor_present": False,
            "docs": [],
        },
    }
    if not given.is_absolute():
        _error(errors, "absolute_path_required", "", "Project path must be absolute.")
        return report
    try:
        root = given.resolve()
    except (OSError, RuntimeError):
        _error(errors, "invalid_project_path", "", "Cannot resolve the project path.")
        return report
    report["project_root"] = str(root)
    if not root.is_dir():
        _error(errors, "missing_project", "", "Project root is not an existing directory.")
        return report
    for name in ("Assets", "Packages", "ProjectSettings"):
        directory = root / name
        if not directory.is_dir():
            _error(errors, "missing_directory", name, "Required Unity directory is missing.")
        elif not _within(directory, root):
            _error(errors, "external_path", name, "Unity directory resolves outside the project.")
    version_path = root / "ProjectSettings" / "ProjectVersion.txt"
    if not _within(version_path, root):
        _error(
            errors, "external_path", "ProjectSettings/ProjectVersion.txt",
            "Unity version metadata resolves outside the project.",
        )
    elif _descriptor_alias(version_path, root):
        _error(
            errors, "protected_descriptor", "ProjectSettings/ProjectVersion.txt",
            "Instance descriptors are never read.",
        )
    else:
        try:
            text = version_path.read_text(encoding="utf-8-sig")
            match = re.search(r"^m_EditorVersion:\s*(\S+)\s*$", text, re.MULTILINE)
            if match and UNITY_VERSION.fullmatch(match.group(1)):
                report["unity_version"] = match.group(1)
            else:
                _error(
                    errors, "invalid_unity_version", "ProjectSettings/ProjectVersion.txt",
                    "A valid m_EditorVersion value is required.",
                )
        except (OSError, UnicodeError):
            _error(
                errors, "missing_unity_version", "ProjectSettings/ProjectVersion.txt",
                "Cannot read the Unity version metadata.",
            )
    manifest = _read_json(root / "Packages" / "manifest.json", root, errors)
    lock = _read_json(root / "Packages" / "packages-lock.json", root, errors)
    records, requested, resolved = _dependencies(manifest, lock, errors)
    report["dependencies"] = records

    files = _inventory(root, warnings)
    mirror_roots: dict[str, dict[str, Any]] = {}
    package_metadata: dict[str, tuple[Path, dict[str, Any]]] = {}
    for path in files:
        relative = path.relative_to(root).as_posix()
        if path.suffix.lower() == ".unity":
            report["scenes"].append(relative)
        elif path.suffix.lower() == ".prefab":
            report["prefabs"].append(relative)
        elif path.suffix.lower() == ".asmdef":
            data = _read_json(path, root, warnings)
            if data is not None:
                if _test_assembly(data):
                    report["test_asmdefs"].append(relative)
                if data.get("name") == "Mirror":
                    mirror_root = path.parent.parent if path.parent.name == "Core" else path.parent
                    mirror_roots[mirror_root.relative_to(root).as_posix()] = {
                        "path": mirror_root.relative_to(root).as_posix(),
                        "kind": "package" if relative.startswith("Packages/") else "asset",
                        "version": None,
                        "version_source": None,
                    }
        elif path.name == "package.json":
            # Package metadata can exist in either embedded packages or imported assets.
            data = _read_json(path, root, warnings)
            if data is None:
                continue
            name = data.get("name")
            if isinstance(name, str) and PACKAGE_NAME.fullmatch(name):
                package_metadata[name] = (path.parent, data)
            if isinstance(name, str) and name in MIRROR_PACKAGES:
                mirror_roots[path.parent.relative_to(root).as_posix()] = {
                    "path": path.parent.relative_to(root).as_posix(),
                    "kind": "package" if relative.startswith("Packages/") else "asset",
                    "version": data.get("version") if (
                        isinstance(data.get("version"), str)
                        and VERSION.fullmatch(data["version"])
                    ) else None,
                    "version_source": relative,
                }
    conventional = root / "Assets" / "Mirror"
    if conventional.is_dir() and _within(conventional, root) and not _linked_directory(conventional):
        mirror_roots.setdefault("Assets/Mirror", {
            "path": "Assets/Mirror", "kind": "asset",
            "version": None, "version_source": None,
        })
    mirror_dependencies = sorted(
        name for name in requested.keys() | resolved.keys()
        if name in MIRROR_PACKAGES
    )
    report["mirror"] = {
        "detected": bool(mirror_roots or mirror_dependencies),
        "roots": [mirror_roots[name] for name in sorted(mirror_roots)],
        "dependencies": mirror_dependencies,
    }

    embedded = root / "Packages" / PIPELINE_PACKAGE
    descriptor = root / "Library" / "Pipeline" / ".unity-pipeline-port"
    embedded_present = embedded.is_dir()
    pipeline = report["pipeline"]
    manifest_declarations = (manifest or {}).get("dependencies", {})
    lock_declarations = (lock or {}).get("dependencies", {})
    pipeline["required"] = (
        embedded_present
        or (
            isinstance(manifest_declarations, dict)
            and PIPELINE_PACKAGE in manifest_declarations
        )
        or (
            isinstance(lock_declarations, dict)
            and PIPELINE_PACKAGE in lock_declarations
        )
    )
    pipeline["embedded_path"] = str(embedded) if embedded_present else None
    pipeline["descriptor_path"] = str(descriptor)
    # No content read: the descriptor contains an authentication token.
    pipeline["descriptor_present"] = descriptor.is_file()
    version = None
    if PIPELINE_PACKAGE in package_metadata:
        package_root, metadata = package_metadata[PIPELINE_PACKAGE]
        if package_root == embedded:
            version = metadata.get("version")
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        version = resolved.get(PIPELINE_PACKAGE, {}).get("version")
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        version = requested.get(PIPELINE_PACKAGE)
    pipeline["package_version"] = (
        version if isinstance(version, str) and VERSION.fullmatch(version) else None
    )
    if embedded_present and _within(embedded, root) and not _linked_directory(embedded):
        pipeline["docs"] = [
            str(path) for path in files
            if path.is_relative_to(embedded)
            and path.suffix.lower() == ".md"
            and (
                path.parent == embedded
                or path.is_relative_to(embedded / "Documentation~")
            )
        ]
    report["valid"] = not errors
    return report


def _write_output(path: str, project_root: str, text: str) -> None:
    output = Path(path).expanduser()
    if not output.is_absolute():
        raise ValueError("Output path must be absolute.")
    if output.suffix.lower() != ".json":
        raise ValueError("Output must use a .json extension.")
    output = output.resolve()
    if _within(output, Path(project_root).resolve()):
        raise ValueError("Output must be outside the Unity project.")
    if not output.parent.is_dir():
        raise ValueError("Output parent directory does not exist.")
    # Replacing the directory entry also avoids modifying a hard-linked project file.
    temporary: str | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="\n",
            dir=output.parent, prefix=".mirror-audit-", suffix=".json", delete=False,
        ) as stream:
            temporary = stream.name
            stream.write(text)
        os.replace(temporary, output)
        temporary = None
    finally:
        if temporary is not None:
            try:
                Path(temporary).unlink()
            except OSError:
                pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, help="Absolute Unity project directory.")
    parser.add_argument(
        "--output", help="Optional absolute .json report outside the Unity project.",
    )
    arguments = parser.parse_args(argv)
    report = audit_project(arguments.project)
    if arguments.output:
        try:
            text = json.dumps(report, ensure_ascii=True, indent=2, sort_keys=True) + "\n"
            _write_output(arguments.output, report["project_root"], text)
        except (OSError, RuntimeError, ValueError):
            _error(
                report["errors"], "invalid_output", "",
                "Cannot write output; use an absolute .json path outside the project.",
            )
            report["valid"] = False
    print(json.dumps(report, ensure_ascii=True, indent=2, sort_keys=True))
    return 0 if report["valid"] else 1


if __name__ == "__main__":
    sys.exit(main())
