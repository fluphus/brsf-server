#!/usr/bin/env python3
"""Verify and join release parts without extracting or changing the resource TAR."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile


def safe_name(value):
    if (not isinstance(value, str)
            or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", value)
            or value.endswith(".")):
        raise ValueError("Manifest filenames must be simple local filenames")
    return value


def positive_size(value):
    if type(value) is not int or value <= 0:
        raise ValueError("Manifest sizes must be positive integers")
    return value


def merge(directory):
    manifest = json.loads((directory / "resources-parts.json").read_text(encoding="utf-8-sig"))
    if type(manifest.get("formatVersion")) is not int or manifest["formatVersion"] != 1:
        raise ValueError("Unsupported manifest version")
    output = directory / safe_name(manifest["output"])
    total = positive_size(manifest["size"])
    parts = manifest["parts"]
    if not isinstance(parts, list) or not parts:
        raise ValueError("Manifest has no parts")
    names = {output.name.casefold()}
    for part in parts:
        name = safe_name(part["name"])
        if name.casefold() in names:
            raise ValueError("Duplicate or conflicting manifest filename: " + name)
        names.add(name.casefold())
        positive_size(part["size"])
        if not isinstance(part["sha256"], str) or not re.fullmatch(r"[0-9a-fA-F]{64}", part["sha256"]):
            raise ValueError("Invalid SHA-256 for " + name)
    if sum(part["size"] for part in parts) != total:
        raise ValueError("Manifest part sizes do not match total size")
    if os.path.lexists(output):
        raise FileExistsError("Output already exists: " + str(output))
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", prefix=".brs-merge-", suffix=".tmp", dir=directory, delete=False) as target:
            temporary = Path(target.name)
            for part in parts:
                print("Verifying " + part["name"], flush=True)
                digest = hashlib.sha256()
                count = 0
                with (directory / part["name"]).open("rb") as source:
                    if os.fstat(source.fileno()).st_size != part["size"]:
                        raise ValueError("Part size mismatch: " + part["name"])
                    while True:
                        chunk = source.read(1024 * 1024)
                        if not chunk:
                            break
                        count += len(chunk)
                        if count > part["size"]:
                            raise ValueError("Part size mismatch: " + part["name"])
                        digest.update(chunk)
                        target.write(chunk)
                if count != part["size"] or digest.hexdigest() != part["sha256"].lower():
                    raise ValueError("Part size or SHA-256 mismatch: " + part["name"])
            target.flush()
            os.fsync(target.fileno())
        # Windows rename refuses an existing destination, including a race with
        # another process, and also works on FAT/exFAT download drives.
        if os.name == "nt":
            os.rename(temporary, output)
            temporary = None
        else:
            # POSIX rename would overwrite a competing output. A same-directory
            # hard link publishes atomically without replacing anything.
            try:
                os.link(temporary, output)
            except FileExistsError:
                raise
            except OSError as error:
                raise OSError("Atomic publication needs hard-link support on this filesystem: " + str(error)) from error
        print("Resource package ready: " + str(output))
    finally:
        if temporary is not None:
            temporary.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path(__file__).resolve().parent,
                        help="folder containing resources-parts.json and all parts (default: script folder)")
    args = parser.parse_args()
    try:
        merge(args.directory.resolve())
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, "Merge failed: " + str(error) + "\n")


if __name__ == "__main__":
    main()
