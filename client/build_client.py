"""Build the unsigned ARM64 game client from the pinned original XAPK (stdlib only)."""
import argparse
import bz2
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
MANIFEST = HERE / "manifest.json"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def file_sha256(path):
    result = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def signature_member(name):
    return name == "stamp-cert-sha256" or bool(re.fullmatch(
        r"META-INF/(MANIFEST\.MF|[^/]+\.(SF|RSA|DSA|EC))", name, re.I))


def unique_members(archive):
    names = archive.namelist()
    require(len(names) == len(set(names)), "Duplicate ZIP entries")
    return set(names)


def check_bytes(data, spec, label):
    require(len(data) == spec["bytes"] and sha256(data) == spec["sha256"],
            "Size/SHA-256 mismatch: " + label)


def signed_offset(data):
    require(len(data) == 8, "Truncated BSDIFF40 integer")
    value = int.from_bytes(data, "little")
    return -(value & ((1 << 63) - 1)) if value >> 63 else value


def apply_bsdiff(old, patch, expected_size):
    """Apply the documented BSDIFF40 format without a native extension.

    Its integers use sign-magnitude encoding; old-file positions outside the
    input contribute zero. Control, diff and extra blocks are bzip2 streams.
    """
    require(len(patch) >= 32 and patch[:8] == b"BSDIFF40", "Invalid BSDIFF40 header")
    control_size, diff_size, size = (signed_offset(patch[p:p + 8]) for p in (8, 16, 24))
    require(control_size >= 0 and diff_size >= 0 and size == expected_size,
            "Invalid BSDIFF40 sizes")
    boundary = 32 + control_size + diff_size
    require(boundary <= len(patch), "Truncated BSDIFF40 blocks")
    control = bz2.decompress(patch[32:32 + control_size])
    diff = bz2.decompress(patch[32 + control_size:boundary])
    extra = bz2.decompress(patch[boundary:])
    result = bytearray(size)
    cp = dp = ep = oldpos = newpos = 0
    while newpos < size:
        require(cp + 24 <= len(control), "Truncated BSDIFF40 control")
        add, copy, seek = (signed_offset(control[p:p + 8]) for p in range(cp, cp + 24, 8))
        cp += 24
        require(add >= 0 and copy >= 0 and newpos + add + copy <= size,
                "Invalid BSDIFF40 control range")
        require(dp + add <= len(diff) and ep + copy <= len(extra), "Truncated BSDIFF40 data")
        result[newpos:newpos + add] = diff[dp:dp + add]
        start, end = max(0, -oldpos), min(add, len(old) - oldpos)
        for index in range(start, end):
            at = newpos + index
            result[at] = (result[at] + old[oldpos + index]) & 255
        newpos += add
        oldpos += add
        dp += add
        result[newpos:newpos + copy] = extra[ep:ep + copy]
        newpos += copy
        ep += copy
        oldpos += seek
    require(cp == len(control) and dp == len(diff) and ep == len(extra),
            "Unused BSDIFF40 trailing data")
    return bytes(result)


def load_manifest():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    require(manifest["schema"] == "brsf.client-payload.v1", "Unsupported client manifest")
    names = [entry["name"] for entry in manifest["entries"]]
    require(len(names) == manifest["payload_entry_count"] == len(set(names)),
            "Invalid payload inventory")
    require(not any(signature_member(name) for name in names), "Manifest contains signatures")
    return manifest


def artifact_bytes(spec):
    relative = PurePosixPath(spec["path"])
    require(not relative.is_absolute() and ".." not in relative.parts,
            "Invalid patch artifact path")
    path = (HERE / relative).resolve()
    require(HERE in path.parents, "Patch artifact escapes client directory")
    data = path.read_bytes()
    check_bytes(data, spec, spec["path"])
    return data


def validate_payload(apk, manifest=None):
    """Validate every unsigned payload entry; signing metadata may be present."""
    manifest = manifest or load_manifest()
    with zipfile.ZipFile(apk) as archive:
        actual = {name for name in unique_members(archive) if not signature_member(name)}
        expected = {entry["name"] for entry in manifest["entries"]}
        require(actual == expected, "APK payload entry inventory mismatch")
        for entry in manifest["entries"]:
            with archive.open(entry["name"]) as stream:
                digest = hashlib.sha256()
                size = 0
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    size += len(chunk)
                    digest.update(chunk)
            require(size == entry["bytes"] and digest.hexdigest() == entry["sha256"],
                    "APK payload mismatch: " + entry["name"])
    return {"entry_count": len(expected), "all_entry_sizes_and_sha256_match": True,
            "reference_payload_sha256": manifest["reference_payload_sha256"]}


def build(xapk_path, output_path, report_path=None):
    manifest = load_manifest()
    xapk_path, output_path = Path(xapk_path).resolve(), Path(output_path).resolve()
    report_path = Path(report_path).resolve() if report_path else None
    require(output_path != xapk_path and not output_path.exists(), "Output must be a new APK path")
    if report_path:
        require(report_path not in (xapk_path, output_path) and not report_path.exists(),
                "Report must be a separate new path")
    require(xapk_path.stat().st_size == manifest["xapk"]["bytes"] and
            file_sha256(xapk_path) == manifest["xapk"]["sha256"], "Original XAPK size/SHA-256 mismatch")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    counts = {"copy": 0, "bsdiff": 0, "add": 0}
    # The two compressed APK inputs are read in memory; no extracted game tree is kept.
    with zipfile.ZipFile(xapk_path) as xapk:
        unique_members(xapk)
        sources = {}
        buffers = []
        try:
            for spec in manifest["source_apks"]:
                data = xapk.read(spec["name"])
                check_bytes(data, spec, spec["name"])
                buffer = io.BytesIO(data)
                buffers.append(buffer)
                archive = sources[spec["name"]] = zipfile.ZipFile(buffer)
                unique_members(archive)
            handle, temporary = tempfile.mkstemp(prefix=".brsf-client-", suffix=".apk", dir=output_path.parent)
            os.close(handle)
            temporary = Path(temporary)
            try:
                with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as target:
                    for entry in manifest["entries"]:
                        action = entry["action"]
                        require(action in counts, "Unknown client build action")
                        if action == "add":
                            data = artifact_bytes(entry["artifact"])
                        else:
                            source = entry["source"]
                            data = sources[source["apk"]].read(source["name"])
                            check_bytes(data, source, source["name"])
                            if action == "bsdiff":
                                data = apply_bsdiff(data, artifact_bytes(entry["artifact"]), entry["bytes"])
                        check_bytes(data, entry, entry["name"])
                        info = zipfile.ZipInfo(entry["name"], date_time=(1980, 1, 1, 0, 0, 0))
                        info.compress_type = entry["compression"]
                        info.create_system = 3
                        info.external_attr = 0o100644 << 16
                        target.writestr(info, data, compresslevel=9)
                        counts[action] += 1
                verification = validate_payload(temporary, manifest)
                os.replace(temporary, output_path)
            finally:
                if temporary.exists():
                    temporary.unlink()
        finally:
            for archive in sources.values():
                archive.close()
            for buffer in buffers:
                buffer.close()
    report = {"schema": "brsf.client-build.v1", "status": "unsigned-client-built",
              "manifest_sha256": file_sha256(MANIFEST), "xapk_sha256": manifest["xapk"]["sha256"],
              "output_sha256": file_sha256(output_path), "output_bytes": output_path.stat().st_size,
              "actions": counts, "payload_verification": verification,
              "signing_performed": False, "reference_apk_required": False}
    if report_path:
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xapk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(build(args.xapk, args.output, args.report), indent=2))
    except (ValueError, OSError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, "Client build failed: " + str(error) + "\n")


if __name__ == "__main__":
    main()
