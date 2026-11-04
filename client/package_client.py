#!/usr/bin/env python3
"""Rebuild, align, and sign the local game client with the caller's own key."""
import argparse
import getpass
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile


HERE = Path(__file__).resolve().parent


def signature(name):
    return name == "stamp-cert-sha256" or bool(re.fullmatch(
        r"META-INF/(MANIFEST\.MF|[^/]+\.(SF|RSA|DSA|EC))", name, re.I))


def payload(path):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("APK contains duplicate ZIP members")
        return {name: hashlib.sha256(archive.read(name)).hexdigest()
                for name in names if not signature(name)}


def run(args, env=None):
    completed = subprocess.run([str(x) for x in args], env=env,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = completed.stdout.decode("utf-8", errors="replace")
    if completed.returncode:
        raise RuntimeError("{} failed (exit {}):\n{}".format(
            Path(args[0]).name, completed.returncode, output.strip()))
    return output


def build_tools(args):
    if args.build_tools:
        candidates = [args.build_tools]
    else:
        roots = [args.sdk, os.environ.get("ANDROID_HOME"),
                 os.environ.get("ANDROID_SDK_ROOT")]
        if os.name == "nt":
            roots.append(str(Path(os.environ.get("LOCALAPPDATA", "~")) / "Android/Sdk"))
        else:
            roots.extend([str(Path.home() / "Android/Sdk"),
                          str(Path.home() / "Library/Android/sdk")])
        candidates = []
        for root in roots:
            if root:
                directory = Path(root).expanduser() / "build-tools"
                if directory.is_dir():
                    candidates.extend(sorted(directory.iterdir(),
                        key=lambda p: tuple(int(x) for x in re.findall(r"\d+", p.name)),
                        reverse=True))
    suffix = ".exe" if os.name == "nt" else ""
    for directory in candidates:
        directory = Path(directory).expanduser().resolve()
        if all((directory / file).is_file() for file in
               ["zipalign" + suffix, "aapt2" + suffix, "lib/apksigner.jar"]):
            return directory, directory / ("zipalign" + suffix), directory / ("aapt2" + suffix)
    raise ValueError("Android SDK Build Tools not found; use --build-tools or --sdk")


def java_executable(value):
    if value:
        executable = shutil.which(value)
    elif os.environ.get("JAVA_HOME"):
        executable = shutil.which(str(Path(os.environ["JAVA_HOME"]) / "bin/java"))
    else:
        executable = shutil.which("java")
    if not executable:
        raise ValueError("Java not found; set JAVA_HOME or use --java")
    return executable


def password(path, prompt):
    if path:
        # apksigner's file: password source also reads the first line only.
        lines = path.read_text(encoding="utf-8-sig").splitlines()
        if not lines:
            raise ValueError("Password file is empty")
        return lines[0]
    if not sys.stdin.isatty():
        raise ValueError("Hidden password prompts require a terminal; use an external --store-pass-file")
    return getpass.getpass(prompt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xapk", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", type=Path, help="Default: OUTPUT.apk.report.json")
    parser.add_argument("--keystore", required=True, type=Path)
    parser.add_argument("--alias", required=True)
    parser.add_argument("--store-pass-file", type=Path, help="External UTF-8 password file; otherwise hidden prompt")
    parser.add_argument("--key-pass-file", type=Path, help="External key password file; default: store password")
    parser.add_argument("--prompt-key-pass", action="store_true", help="Prompt for a different private-key password")
    parser.add_argument("--sdk", type=Path, help="Android SDK root")
    parser.add_argument("--build-tools", type=Path, help="Build Tools version directory (e.g. SDK/build-tools/36.0.0)")
    parser.add_argument("--java", help="Java executable; defaults to JAVA_HOME/bin/java or PATH")
    args = parser.parse_args()
    if args.key_pass_file and args.prompt_key_pass:
        parser.error("--key-pass-file and --prompt-key-pass cannot be combined")
    output = args.output.expanduser().resolve()
    report_path = (args.report or Path(str(output) + ".report.json")).expanduser().resolve()
    for path in [output, report_path]:
        if path.exists():
            raise ValueError("Refusing to overwrite existing output: " + str(path))
    if output == report_path:
        raise ValueError("APK and report paths must differ")
    if not args.keystore.is_file():
        raise ValueError("Keystore does not exist")
    directory, zipalign, aapt2 = build_tools(args)
    java = java_executable(args.java)
    run([java, "-version"])
    store_password = password(args.store_pass_file, "Keystore password: ")
    key_password = (password(args.key_pass_file, "Private key password: ")
                    if args.key_pass_file or args.prompt_key_pass else store_password)
    child_env = os.environ.copy()
    child_env["BRS_CLIENT_STORE_PASSWORD"] = store_password
    child_env["BRS_CLIENT_KEY_PASSWORD"] = key_password
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="brsf-client-") as scratch:
        temp = Path(scratch)
        unsigned, aligned, signed = [temp / name for name in
                                     ["unsigned.apk", "aligned.apk", "signed.apk"]]
        build_report = temp / "build-report.json"
        print("Rebuilding client payload...", flush=True)
        run([sys.executable, "-B", HERE / "build_client.py", "--xapk", args.xapk,
             "--output", unsigned, "--report", build_report])
        expected_payload = payload(unsigned)
        print("Aligning and signing...", flush=True)
        run([zipalign, "-f", "4", unsigned, aligned])
        run([java, "-jar", directory / "lib/apksigner.jar", "sign",
             "--ks", args.keystore, "--ks-key-alias", args.alias,
             "--ks-pass", "env:BRS_CLIENT_STORE_PASSWORD",
             "--key-pass", "env:BRS_CLIENT_KEY_PASSWORD",
             "--v1-signing-enabled", "true", "--v2-signing-enabled", "true",
             "--v3-signing-enabled", "true", "--v4-signing-enabled", "false",
             "--out", signed, aligned], env=child_env)
        del child_env["BRS_CLIENT_STORE_PASSWORD"], child_env["BRS_CLIENT_KEY_PASSWORD"]
        verification = run([java, "-jar", directory / "lib/apksigner.jar", "verify",
                            "--verbose", "--print-certs", signed])
        run([zipalign, "-c", "4", signed])
        if payload(signed) != expected_payload:
            raise ValueError("Signing changed the game payload")
        badging = run([aapt2, "dump", "badging", signed])
        for required in ["name='jp.goodsmile.brsfragment'", "versionCode='203'",
                         "versionName='2.5.0'", "targetSdkVersion:'33'"]:
            if required not in badging:
                raise ValueError("Unexpected manifest identity or SDK: " + required)
        if not re.search(r"(?:minSdkVersion|sdkVersion):'26'", badging):
            raise ValueError("Unexpected minimum SDK")
        manifest = run([aapt2, "dump", "xmltree", "--file", "AndroidManifest.xml", signed])
        if any(marker in manifest for marker in ["requiredSplitTypes=", ":isSplitRequired(",
               '="com.android.vending.splits"', '="com.android.vending.splits.required"']):
            raise ValueError("Manifest still requires APK splits")
        for flag in ["debuggable", "testOnly"]:
            if re.search(r"android:" + flag + r"\([^\n]*\)=\(type 0x12\)0xffffffff", manifest):
                raise ValueError("Manifest unexpectedly enables " + flag)
        report = {"schema": "brsf.client-package.v1", "status": "signed-client-built",
                  "signing_performed": True,
                  "build": json.loads(build_report.read_text(encoding="utf-8"))}
        report["packaging"] = {
            "buildTools": directory.name,
            "apkSha256": hashlib.sha256(signed.read_bytes()).hexdigest(),
            "bytes": signed.stat().st_size,
            "payloadMembers": len(expected_payload),
            "signedPayloadUnchanged": True,
            "zipAlignmentBytes": 4,
            "signatureVerification": verification.strip().splitlines(),
            "package": "jp.goodsmile.brsfragment", "versionCode": 203,
            "versionName": "2.5.0", "minSdk": 26, "targetSdk": 33,
            "manifestSplitRequirementsRemoved": True,
            "deviceOperations": False,
        }
        # Exclusive creation protects earlier build artifacts from accidental replacement.
        with output.open("xb") as target, signed.open("rb") as source:
            shutil.copyfileobj(source, target)
        with report_path.open("x", encoding="utf-8", newline="\n") as target:
            json.dump(report, target, ensure_ascii=False, indent=2)
            target.write("\n")
    print("APK: " + str(output))
    print("Report: " + str(report_path))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as error:
        print("Error: " + str(error), file=sys.stderr)
        sys.exit(1)
