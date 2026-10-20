#!/usr/bin/env python3
"""Build the app-layer update package next to a full publish output.

The .NET runtime is ~95 MB of a self-contained publish. It changes only when the
runtime patch changes, while the app layer (Mutter/EF Core/SQLite/wwwroot) is
~5 MB compressed. This script compares the publish output with the installed
runtime packs and emits a zip containing only the app layer plus a file manifest.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone

RUNTIME_CORE_NAMES = {
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "clrjit.dll",
    "system.private.corelib.dll",
    "libcoreclr.so",
    "libhostfxr.so",
    "libhostpolicy.so",
    "libclrjit.so",
    "mscordaccore.dll",
    "mscordbi.dll",
    "libmscordaccore.so",
    "libmscordbi.so",
    "createdump",
    "createdump.exe",
}


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def find_dotnet_root():
    env = os.environ.get("DOTNET_ROOT")
    if env and os.path.isdir(os.path.join(env, "shared")):
        return env

    exe = shutil.which("dotnet")
    if exe:
        root = os.path.dirname(os.path.realpath(exe))
        if os.path.isdir(os.path.join(root, "shared")):
            return root

    try:
        output = subprocess.check_output(
            ["dotnet", "--info"], text=True, errors="replace")
    except (OSError, subprocess.SubprocessError):
        return None

    match = re.search(r"Base Path:\s*(.+)", output)
    if not match:
        return None
    base = match.group(1).strip()
    root = os.path.dirname(os.path.dirname(os.path.abspath(base)))
    return root if os.path.isdir(os.path.join(root, "shared")) else None


def runtime_config(publish_dir):
    path = os.path.join(publish_dir, "mutter-bot.runtimeconfig.json")
    with open(path, "r", encoding="utf-8") as handle:
        config = json.load(handle)
    options = config.get("runtimeOptions", {})
    included = options.get("includedFrameworks") or []
    for framework in included:
        if framework.get("name") == "Microsoft.NETCore.App":
            return framework.get("version")
    return None


def collect_runtime_hashes(dotnet_root, rid, runtime_version):
    if not dotnet_root or not runtime_version:
        return {}

    roots = []
    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        roots.append(os.path.join(dotnet_root, "shared", framework, runtime_version))

    pack_patterns = [
        os.path.join(dotnet_root, "packs", f"Microsoft.NETCore.App.Runtime.{rid}", "*", "runtimes", rid),
        os.path.join(dotnet_root, "packs", f"Microsoft.AspNetCore.App.Runtime.{rid}", "*", "runtimes", rid),
        os.path.join(dotnet_root, "packs", f"Microsoft.NETCore.App.Host.{rid}", "*", "runtimes", rid, "native"),
    ]
    for pattern in pack_patterns:
        import glob
        roots.extend(glob.glob(pattern))

    home = os.path.expanduser("~")
    for pattern in (
        os.path.join(home, ".nuget", "packages", f"microsoft.netcore.app.runtime.{rid}", "*", "runtimes", rid),
        os.path.join(home, ".nuget", "packages", f"microsoft.aspnetcore.app.runtime.{rid}", "*", "runtimes", rid),
    ):
        import glob
        roots.extend(glob.glob(pattern))

    hashes = {}
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dirpath, _, filenames in os.walk(root):
            for filename in filenames:
                path = os.path.join(dirpath, filename)
                try:
                    hashes.setdefault(filename, set()).add(sha256(path))
                except OSError:
                    continue
    return hashes


def protected(relative):
    first = relative.replace("\\", "/").split("/")[0]
    lowered = first.lower()
    if lowered in ("logs", ".update", ".backup"):
        return True
    if lowered.startswith("appsettings") and lowered.endswith(".json"):
        return True
    if lowered in ("update-settings.json", "app-layer.json", "secrets.json", "cookies.txt"):
        return True
    if lowered.endswith(".log"):
        return True
    if lowered in ("mutter-bot.db", "mutter-bot.db-wal", "mutter-bot.db-shm"):
        return True
    return ".bak." in lowered


def is_runtime_file(relative, path, runtime_hashes):
    name = os.path.basename(relative)
    if name.lower() in RUNTIME_CORE_NAMES:
        return True
    if name.startswith("mscordaccore_"):
        return True
    return name in runtime_hashes and sha256(path) in runtime_hashes[name]


def iter_publish_files(publish_dir):
    for dirpath, dirnames, filenames in os.walk(publish_dir):
        dirnames[:] = sorted(d for d in dirnames if d.lower() not in ("logs", ".update", ".backup"))
        for filename in sorted(filenames):
            path = os.path.join(dirpath, filename)
            relative = os.path.relpath(path, publish_dir).replace(os.sep, "/")
            yield relative, path


def write_zip(zip_path, package_root, app_files, manifest):
    fixed_time = (2026, 1, 1, 0, 0, 0)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for relative in sorted(app_files):
            source = os.path.join(package_root, relative.replace("/", os.sep))
            info = zipfile.ZipInfo(f"mutter-bot/{relative}", fixed_time)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(source, "rb") as handle:
                archive.writestr(info, handle.read())

        info = zipfile.ZipInfo("mutter-bot/app-layer.json", fixed_time)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        archive.writestr(info, json.dumps(manifest, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--flavor", required=True, choices=["self-contained", "framework-dependent"])
    parser.add_argument("--version", required=True)
    parser.add_argument("--full-archive", required=True)
    parser.add_argument("--out-dir", required=True)
    parser.add_argument("--dotnet-root", default=None)
    parser.add_argument("--no-app", action="store_true",
                        help="only emit the full asset (used for framework-dependent packages)")
    extra = parser.parse_args()

    publish_dir = os.path.abspath(extra.publish_dir)
    out_dir = os.path.abspath(extra.out_dir)
    os.makedirs(out_dir, exist_ok=True)

    runtime_version = None
    if extra.flavor == "self-contained":
        runtime_version = runtime_config(publish_dir)
        if not runtime_version:
            print("self-contained publish has no includedFrameworks version", file=sys.stderr)
            return 1

    full_path = os.path.abspath(extra.full_archive)
    full_asset = {
        "name": os.path.basename(full_path),
        "kind": "full",
        "rid": extra.rid,
        "flavor": extra.flavor,
        "runtimeVersion": runtime_version,
        "size": os.path.getsize(full_path),
        "sha256": sha256(full_path),
        "url": os.path.basename(full_path),
    }
    fragment_name = f"update-fragment-{extra.rid}-{extra.flavor}.json"

    if extra.no_app:
        with open(os.path.join(out_dir, fragment_name), "w", encoding="utf-8") as handle:
            json.dump({"assets": [full_asset]}, handle, ensure_ascii=False, indent=2)
        print(json.dumps({
            "rid": extra.rid,
            "flavor": extra.flavor,
            "runtimeVersion": runtime_version,
            "fullBytes": os.path.getsize(full_path),
            "fragment": fragment_name,
        }, ensure_ascii=False))
        return 0

    runtime_hashes = {}
    if extra.flavor == "self-contained":
        dotnet_root = extra.dotnet_root or find_dotnet_root()
        runtime_hashes = collect_runtime_hashes(dotnet_root, extra.rid, runtime_version)
        if not runtime_hashes:
            print("could not locate .NET runtime packs; refusing to guess the app layer", file=sys.stderr)
            return 1

    app_files = []
    runtime_files = 0
    for relative, path in iter_publish_files(publish_dir):
        if protected(relative):
            continue
        if extra.flavor == "self-contained" and is_runtime_file(relative, path, runtime_hashes):
            runtime_files += 1
            continue
        app_files.append(relative)

    if not app_files:
        print("no app-layer files found", file=sys.stderr)
        return 1

    exe = "mutter-bot.exe" if extra.rid.startswith("win-") else "mutter-bot"
    if exe not in app_files:
        print(f"app layer is missing {exe}", file=sys.stderr)
        return 1

    files = []
    for relative in sorted(app_files):
        path = os.path.join(publish_dir, relative.replace("/", os.sep))
        files.append({
            "path": relative,
            "size": os.path.getsize(path),
            "sha256": sha256(path),
        })

    manifest = {
        "schema": 1,
        "version": extra.version,
        "rid": extra.rid,
        "flavor": extra.flavor,
        "runtimeVersion": runtime_version,
        "files": files,
        "delete": [],
    }

    app_name = f"mutter-bot-v{extra.version}-{extra.rid}-{extra.flavor}-app.zip"
    app_path = os.path.join(out_dir, app_name)
    write_zip(app_path, publish_dir, app_files, manifest)

    full_path = os.path.abspath(extra.full_archive)
    fragment = {
        "assets": [
            full_asset,
            {
                "name": app_name,
                "kind": "app",
                "rid": extra.rid,
                "flavor": extra.flavor,
                "runtimeVersion": runtime_version,
                "size": os.path.getsize(app_path),
                "sha256": sha256(app_path),
                "url": app_name,
            },
        ]
    }

    with open(os.path.join(out_dir, fragment_name), "w", encoding="utf-8") as handle:
        json.dump(fragment, handle, ensure_ascii=False, indent=2)

    print(json.dumps({
        "rid": extra.rid,
        "flavor": extra.flavor,
        "runtimeVersion": runtime_version,
        "runtimeFiles": runtime_files,
        "appFiles": len(app_files),
        "appBytes": os.path.getsize(app_path),
        "fullBytes": os.path.getsize(full_path),
        "appAsset": app_name,
        "fragment": fragment_name,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
