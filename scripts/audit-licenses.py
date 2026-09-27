#!/usr/bin/env python3
"""Fail-closed license gate over the restored NuGet graph (standard library only).

This checks package license declarations and exact reviewed legacy manifests,
not legal advice or a substitute for preserving bundled native-code notices.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import sys
import xml.etree.ElementTree as ET

FORBIDDEN_IDS = ("itext", "itext7", "itextsharp", "itext.pdfsweep", "itext7.pdfsweep", "libvlcsharp")
PERMISSIVE = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "Zlib", "0BSD", "BSL-1.0", "Unlicense", "CC0-1.0", "FTL", "Unicode-3.0"}


def permissive_expression(expression: str) -> bool:
    """Only accept well-formed AND/OR expressions whose every choice is permissive."""
    tokens = re.findall(r"[A-Za-z0-9.+-]+|[()]", expression)
    if re.sub(r"\s+", "", expression) != "".join(tokens) or not tokens:
        return False
    position = 0

    def atom() -> bool:
        nonlocal position
        if position == len(tokens):
            return False
        token = tokens[position]
        position += 1
        if token == "(":
            if not expression_part() or position == len(tokens) or tokens[position] != ")":
                return False
            position += 1
            return True
        return token in PERMISSIVE

    def expression_part() -> bool:
        nonlocal position
        if not atom():
            return False
        while position < len(tokens) and tokens[position] in ("AND", "OR"):
            position += 1
            if not atom():
                return False
        return True

    return expression_part() and position == len(tokens)


def forbidden_id(package: str) -> bool:
    name = package.lower().split("/")[0]
    return any(name == item or name.startswith(item + ".") for item in FORBIDDEN_IDS)


def inspect_package(key: str, directory: pathlib.Path, policy: dict) -> dict:
    if forbidden_id(key):
        raise ValueError(f"Restricted PDF/media dependency: {key}")
    manifests = list(directory.glob("*.nuspec"))
    if len(manifests) != 1:
        raise ValueError(f"Missing/ambiguous package manifest: {key}")
    manifest = manifests[0]
    raw = manifest.read_bytes()
    root = ET.fromstring(raw)
    license_node = next((node for node in root.iter() if node.tag.split('}')[-1] == 'license'), None)
    expression = license_node.text.strip() if license_node is not None and license_node.text else ""
    kind = license_node.get("type") if license_node is not None else None
    record = {"package": key, "declaration_type": kind, "declared_license": expression,
              "manifest_sha256": hashlib.sha256(raw).hexdigest()}
    if kind == "expression" and permissive_expression(expression):
        record["accepted_license"] = expression
    else:
        exception = policy.get("reviewed", {}).get(key.lower())
        if not exception or not permissive_expression(exception["license"]):
            raise ValueError(f"Unapproved or missing permissive license: {key} ({expression or kind or 'no declaration'})")
        expected_manifest = exception.get("manifest_sha256")
        if expected_manifest and expected_manifest != record["manifest_sha256"]:
            raise ValueError(f"Reviewed manifest changed: {key}")
        if kind == "file":
            license_file = (directory / expression).resolve()
            if not license_file.is_relative_to(directory.resolve()) or not license_file.is_file():
                raise ValueError(f"Unsafe or missing license file: {key}")
            digest = hashlib.sha256(license_file.read_bytes()).hexdigest()
            if digest != exception.get("license_file_sha256"):
                raise ValueError(f"Reviewed license file changed: {key}")
            record["license_file_sha256"] = digest
        elif not expected_manifest:
            raise ValueError(f"Legacy package exception lacks an exact manifest hash: {key}")
        record.update(accepted_license=exception["license"], review_source=exception["source"], review_note=exception["note"])
    hashes = list(directory.glob("*.nupkg.sha512"))
    if hashes:
        record["package_sha512"] = hashes[0].read_text().strip()
    return record


def audit(root: pathlib.Path, policy_path: pathlib.Path, allow_partial: bool = False, notices: pathlib.Path | None = None) -> dict:
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    projects = [p for p in root.rglob("*.csproj") if not {"bin", "obj", ".git", "artifacts"}.intersection(p.parts)]
    if not projects:
        raise ValueError("No project files found; an empty license audit is not success.")
    records: dict[str, dict] = {}
    checked = []
    for project in projects:
        assets = list((project.parent / "obj").rglob("project.assets.json")) if (project.parent / "obj").exists() else []
        if not assets:
            if allow_partial:
                continue
            raise ValueError(f"Restore every project before auditing; no assets for {project.relative_to(root)}")
        for asset in assets:
            graph = json.loads(asset.read_text(encoding="utf-8"))
            if any(item.get("level", "").lower() == "error" for item in graph.get("logs", [])):
                raise ValueError(f"Restore errors in {asset}; an incomplete graph cannot pass the gate.")
            for key, value in graph.get("libraries", {}).items():
                if value["type"] != "package":
                    continue
                directories = [pathlib.Path(folder) / value["path"] for folder in graph["packageFolders"]]
                directory = next((p for p in directories if p.is_dir()), None)
                if directory is None:
                    raise ValueError(f"Restored package bytes missing: {key}")
                record = inspect_package(key, directory, policy)
                if key in records and record != records[key]:
                    raise ValueError(f"Inconsistent package content across project graphs: {key}")
                records[key] = record
                if notices is not None:
                    target = notices / value["path"]
                    target.mkdir(parents=True, exist_ok=True)
                    for candidate in directory.rglob("*"):
                        name = candidate.name.lower()
                        if not candidate.is_file() or candidate.stat().st_size > 2 * 1024 * 1024:
                            continue
                        if not (name.endswith(".nuspec") or "license" in name or "notice" in name or name.startswith("copyright")):
                            continue
                        try:
                            candidate.read_text(encoding="utf-8-sig")
                        except UnicodeError:
                            continue
                        relative = candidate.relative_to(directory)
                        destination = target / relative
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(candidate, destination)
            checked.append(str(asset.relative_to(root)))
    if not checked or not records:
        raise ValueError("No restored packages inspected.")
    for props in list(root.glob("Directory.*.props")) + projects:
        tree = ET.parse(props)
        for node in tree.iter():
            if node.tag.split('}')[-1] in ("PackageReference", "PackageVersion") and forbidden_id(node.get("Include", "")):
                raise ValueError(f"Restricted direct package declaration in {props}: {node.get('Include')}")
    return {"policy": "permissive-only", "scope": "resolved NuGet package declarations and reviewed exact exceptions",
            "partial": allow_partial, "projects": sorted(set(checked)), "packages": [records[key] for key in sorted(records, key=str.lower)]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    parser.add_argument("--policy", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path("artifacts/licenses.json"))
    parser.add_argument("--notices", type=pathlib.Path, default=pathlib.Path("artifacts/third-party-notices"))
    parser.add_argument("--allow-partial", action="store_true", help="Local-only diagnostics; CI must audit all projects")
    args = parser.parse_args()
    try:
        root = args.root.resolve()
        result = audit(root, args.policy or root / "scripts/license-policy.json", args.allow_partial, args.notices)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(f"PASS: {len(result['packages'])} package licenses across {len(result['projects'])} project graphs; {args.output}")
        return 0
    except (ValueError, OSError, KeyError, ET.ParseError) as error:
        print(f"LICENSE AUDIT FAILED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
