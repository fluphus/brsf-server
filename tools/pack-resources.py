#!/usr/bin/env python3
"""Developer-only assembly of the single user-imported resource TAR.

Inputs are explicit reviewed material and original content.zip paths. This tool
does not extract a key, search another workspace, or read device/user data.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import tarfile

ARCHIVE_LENGTH = 8157596864


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--materials", type=Path, required=True)
    parser.add_argument("--content", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[1]
    inventory = (source / "server/Content/resource-package-manifest.json").read_bytes()
    entries = json.loads(inventory)["files"]
    if args.output.exists():
        parser.error("output already exists; choose a new path")
    if args.content.stat().st_size != ARCHIVE_LENGTH:
        parser.error("unsupported content.zip length")
    for entry in entries:
        path = args.materials / entry["path"]
        if path.is_symlink() or any(p.is_symlink() for p in path.parents):
            parser.error("material paths cannot contain links")
        data = path.read_bytes()
        if len(data) != entry["length"] or hashlib.sha256(data).hexdigest() != entry["sha256"]:
            parser.error("material identity differs: " + entry["path"])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    created = False
    try:
        # Exclusive output and plain TAR preserve a bounded-memory streaming import.
        with args.output.open("xb") as output:
            created = True
            write_archive(output, inventory, entries, args)
    except BaseException:
        if created and args.output.exists():
            args.output.unlink()
        raise
    print(f"Resource package created: {len(entries)} materials + unchanged content.zip; {args.output.stat().st_size} bytes")


def write_archive(output, inventory, entries, args):
    with tarfile.open(fileobj=output, mode="w", format=tarfile.GNU_FORMAT) as tar:
        def add(name, stream, size):
            info = tarfile.TarInfo(name)
            info.size = size
            info.mode = 0o600
            info.mtime = 0
            tar.addfile(info, stream)
        add("resource-package-manifest.json", io.BytesIO(inventory), len(inventory))
        for entry in entries:
            with (args.materials / entry["path"]).open("rb") as stream:
                add("materials/" + entry["path"], stream, entry["length"])
        with args.content.open("rb") as stream:
            add("content.zip", stream, ARCHIVE_LENGTH)

if __name__ == "__main__":
    main()
