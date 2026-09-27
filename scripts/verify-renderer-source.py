#!/usr/bin/env python3
"""Verify the source-pinned Apache-2.0 rendering adapter and required notices.

This is a reproducible source inventory, not a legal or security certification.
Only the Python standard library is used; no source is fetched or executed.
"""
from __future__ import annotations
import hashlib
import json
import pathlib
import sys

PIN = "e4476d80f98bf1a5a7cd6f7d45fb7c2afe10ec11"
REPOSITORY = "https://github.com/BobLd/PdfPig.Rendering.Skia"
LOCATION = pathlib.Path("src/ProPDF.Engine.PdfPig/Compatibility/PdfPig.Skia")


def canonical_hash(path: pathlib.Path) -> str:
    # Git checkout can convert LF into CRLF. Canonical UTF-8/LF is independent of host git settings.
    return hashlib.sha256(path.read_text(encoding="utf-8").encode("utf-8")).hexdigest()


def verify(directory: pathlib.Path) -> int:
    directory = directory.resolve()
    manifest = json.loads((directory / "PROVENANCE.json").read_text(encoding="utf-8"))
    if manifest.get("commit") != PIN or manifest.get("repository") != REPOSITORY or manifest.get("license") != "Apache-2.0":
        raise ValueError("Rendering source provenance changed without a reviewed pin update.")
    paths: set[str] = set()
    for item in manifest["files"]:
        name = item["path"]
        relative = pathlib.PurePosixPath(name)
        if relative.is_absolute() or ".." in relative.parts or "\\" in name or name in paths:
            raise ValueError("Unsafe or duplicate source path: " + name)
        source = directory / name
        target = source.resolve()
        if source.is_symlink() or not target.is_relative_to(directory) or not target.is_file():
            raise ValueError("Missing or unsafe rendering source: " + name)
        if canonical_hash(target) != item["vendored_sha256"]:
            raise ValueError("Rendering source hash mismatch: " + name)
        if not isinstance(item.get("upstream_sha256"), str) or len(item["upstream_sha256"]) != 64:
            raise ValueError("Missing upstream source hash: " + name)
        if target.suffix == ".cs":
            text = target.read_text(encoding="utf-8")
            if "Apache License" not in text or "Modified by ProPDF" not in text:
                raise ValueError("Missing copyright/modification notice: " + name)
        paths.add(name)
    required = {"LICENSE.txt", "NOTICE.txt"}
    if not required.issubset(paths) or not (directory / "PATCHES.md").is_file():
        raise ValueError("Rendering adapter is missing its required notices.")
    actual = {p.relative_to(directory).as_posix() for p in directory.rglob("*") if p.is_file()}
    if actual != paths | {"PROVENANCE.json", "PATCHES.md"}:
        raise ValueError("Unreviewed rendering source addition or removal.")
    return len(paths)


if __name__ == "__main__":
    try:
        count = verify(pathlib.Path(__file__).resolve().parent.parent / LOCATION)
        print(f"PASS: {count} source/notice files at pinned renderer {PIN}.")
    except (ValueError, KeyError, OSError, TypeError) as error:
        print(f"Renderer source verification failed: {error}", file=sys.stderr)
        sys.exit(1)
