"""Maintainer-only delta generation; end-user builds never need the reference APK.

Install requirements-generation.txt in a separate virtual environment first.
Output is a new directory so an accepted patch set cannot be overwritten.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile

from build_client import apply_bsdiff, signature_member, unique_members

XAPK_SHA256 = "d98b0c00c999b67cb49d77fa15aafa431a03df59219e72f54a535293b5637b0f"
REFERENCE_SHA256 = "c13451459ac853f78b585e3a2087c2667c5a12645ba1e233d84442023c03394d"


def info(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def payload_digest(entries):
    inventory = [{key: entry[key] for key in ("name", "bytes", "sha256")} for entry in entries]
    return hashlib.sha256(json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def main():
    import bsdiff4
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xapk", type=Path, required=True)
    parser.add_argument("--reference-apk", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    original, accepted = args.xapk.read_bytes(), args.reference_apk.read_bytes()
    if info(original)["sha256"] != XAPK_SHA256 or info(accepted)["sha256"] != REFERENCE_SHA256:
        parser.error("Original XAPK or accepted reference APK hash mismatch")
    args.output_dir.mkdir(parents=True, exist_ok=False)
    (args.output_dir / "patches").mkdir()
    (args.output_dir / "additions").mkdir()
    manifest = {"schema": "brsf.client-payload.v1", "package": "jp.goodsmile.brsfragment",
                "version_name": "2.5.0", "abi": "arm64-v8a", "xapk": info(original),
                "reference_apk_sha256": REFERENCE_SHA256,
                "reference_purpose": "Payload audit anchor only; never a build input",
                "delta_format": "BSDIFF40", "generator": "bsdiff4==1.2.6",
                "source_apks": [], "entries": []}
    buffers, sources = [], {}
    try:
        with zipfile.ZipFile(io.BytesIO(original)) as xapk:
            unique_members(xapk)
            for name in sorted(n for n in xapk.namelist() if n.endswith(".apk")):
                data = xapk.read(name)
                manifest["source_apks"].append({"name": name, **info(data)})
                buffer = io.BytesIO(data)
                buffers.append(buffer)
                sources[name] = zipfile.ZipFile(buffer)
                unique_members(sources[name])
        with zipfile.ZipFile(io.BytesIO(accepted)) as reference:
            unique_members(reference)
            serial = 0
            for name in sorted(n for n in reference.namelist() if not signature_member(n)):
                target = reference.read(name)
                entry = {"name": name, **info(target), "compression": reference.getinfo(name).compress_type}
                candidates = [(apk, archive.read(name)) for apk, archive in sources.items() if name in archive.namelist()]
                equal = next(((apk, data) for apk, data in candidates if data == target), None)
                source = equal or next(((apk, data) for apk, data in candidates
                                       if apk == "jp.goodsmile.brsfragment.apk"), None) or next(iter(candidates), None)
                if source:
                    apk, prior = source
                    entry["source"] = {"apk": apk, "name": name, **info(prior)}
                    entry["action"] = "copy" if equal else "bsdiff"
                    if not equal:
                        serial += 1
                        print("Generating BSDIFF40:", name, flush=True)
                        patch = bsdiff4.diff(prior, target)
                        if bsdiff4.patch(prior, patch) != target or apply_bsdiff(prior, patch, len(target)) != target:
                            raise ValueError("Delta round-trip mismatch: " + name)
                        relative = "patches/%03d.bsdiff" % serial
                        (args.output_dir / relative).write_bytes(patch)
                        entry["artifact"] = {"path": relative, **info(patch)}
                        print("Delta bytes:", len(patch), flush=True)
                else:
                    serial += 1
                    relative = "additions/%03d-network_security_config.axml" % serial
                    if name != "res/xml/network_security_config.xml":
                        raise ValueError("Unexpected new payload entry: " + name)
                    entry["action"] = "add"
                    (args.output_dir / relative).write_bytes(target)
                    entry["artifact"] = {"path": relative, **info(target)}
                manifest["entries"].append(entry)
        manifest["payload_entry_count"] = len(manifest["entries"])
        manifest["reference_payload_sha256"] = payload_digest(manifest["entries"])
        (args.output_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"payload_entries": len(manifest["entries"]),
                          "artifact_bytes": sum(e.get("artifact", {}).get("bytes", 0) for e in manifest["entries"])}))
    finally:
        for archive in sources.values():
            archive.close()
        for buffer in buffers:
            buffer.close()


if __name__ == "__main__":
    main()
