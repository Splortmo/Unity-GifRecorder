#!/usr/bin/env python3
"""Builds a .unitypackage from a folder under Assets/ without needing the Unity Editor.

A .unitypackage is a gzipped tar where every asset lives in a folder named after its GUID:
    <guid>/asset       - file contents (omitted for folders)
    <guid>/asset.meta  - the .meta file
    <guid>/pathname    - project-relative path, e.g. Assets/GifRecorder/Runtime/ScreenRecorder.cs

Usage:
    build_unitypackage.py <Assets/Folder> <output.unitypackage> [--extra SRC:DEST ...]

--extra adds a file from outside the folder (e.g. README.md) at DEST inside the package;
a .meta with a stable GUID is generated for it.
"""
import argparse
import gzip
import hashlib
import io
import os
import re
import sys
import tarfile

GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)

GENERATED_META = """fileFormatVersion: 2
guid: {guid}
{importer}:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def read_guid(meta_path):
    with open(meta_path, "r", encoding="utf-8") as f:
        m = GUID_RE.search(f.read())
    if not m:
        sys.exit(f"error: no guid in {meta_path}")
    return m.group(1).lower()


def add_bytes(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    info.mtime = 0
    tar.addfile(info, io.BytesIO(data))


def add_entry(tar, guid, pathname, meta_bytes, asset_bytes):
    add_bytes(tar, f"{guid}/pathname", pathname.encode("utf-8"))
    add_bytes(tar, f"{guid}/asset.meta", meta_bytes)
    if asset_bytes is not None:
        add_bytes(tar, f"{guid}/asset", asset_bytes)


def collect(root):
    """Yields (path_on_disk, is_dir) for root and everything under it that has a .meta."""
    root = root.rstrip("/\\")
    yield root, True
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for d in dirnames:
            yield os.path.join(dirpath, d), True
        for f in sorted(filenames):
            if not f.endswith(".meta"):
                yield os.path.join(dirpath, f), False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", help="Folder to package, relative to the project root (e.g. Assets/GifRecorder)")
    ap.add_argument("output")
    ap.add_argument("--extra", action="append", default=[], metavar="SRC:DEST")
    args = ap.parse_args()

    if not os.path.isdir(args.folder):
        sys.exit(f"error: {args.folder} is not a directory")

    seen = set()
    count = 0
    with open(args.output, "wb") as raw, \
            gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as gz, \
            tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tar:

        for path, is_dir in collect(args.folder):
            meta = path + ".meta"
            if not os.path.isfile(meta):
                print(f"warning: skipping {path} (no .meta)", file=sys.stderr)
                continue
            guid = read_guid(meta)
            if guid in seen:
                sys.exit(f"error: duplicate guid {guid} at {path}")
            seen.add(guid)
            with open(meta, "rb") as f:
                meta_bytes = f.read()
            asset_bytes = None
            if not is_dir:
                with open(path, "rb") as f:
                    asset_bytes = f.read()
            add_entry(tar, guid, path.replace(os.sep, "/"), meta_bytes, asset_bytes)
            count += 1

        for spec in args.extra:
            src, _, dest = spec.partition(":")
            if not dest:
                sys.exit(f"error: --extra expects SRC:DEST, got {spec}")
            # Stable GUID derived from the destination path so upgrades replace the same asset.
            guid = hashlib.md5(dest.encode("utf-8")).hexdigest()
            if guid in seen:
                sys.exit(f"error: guid collision for extra {dest}")
            seen.add(guid)
            importer = "TextScriptImporter" if dest.lower().endswith((".md", ".txt")) else "DefaultImporter"
            meta_bytes = GENERATED_META.format(guid=guid, importer=importer).encode("utf-8")
            with open(src, "rb") as f:
                add_entry(tar, guid, dest, meta_bytes, f.read())
            count += 1

    print(f"Wrote {args.output} ({count} assets)")


if __name__ == "__main__":
    main()
