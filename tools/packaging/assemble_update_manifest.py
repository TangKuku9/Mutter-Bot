#!/usr/bin/env python3
"""Merge per-platform update fragments into the signed update.json manifest."""

import argparse
import glob
import json
import os
import sys
from datetime import datetime, timezone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fragments-dir", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    assets = []
    seen = set()
    for path in sorted(glob.glob(os.path.join(args.fragments_dir, "update-fragment-*.json"))):
        with open(path, "r", encoding="utf-8") as handle:
            fragment = json.load(handle)
        for asset in fragment.get("assets", []):
            name = asset.get("name", "")
            if not name or name in seen:
                print(f"duplicate or empty asset name: {name}", file=sys.stderr)
                return 1
            seen.add(name)
            assets.append(asset)

    if not assets:
        print("no update fragments found", file=sys.stderr)
        return 1

    assets.sort(key=lambda item: (item.get("rid", ""), item.get("flavor", ""), item.get("kind", "")))
    manifest = {
        "schema": 1,
        "version": args.version,
        "tag": args.tag,
        "releaseUrl": f"https://github.com/{args.repo}/releases/tag/{args.tag}",
        "publishedAt": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "assets": assets,
    }

    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    print(json.dumps({
        "version": manifest["version"],
        "assets": len(assets),
        "appAssets": sum(1 for item in assets if item.get("kind") == "app"),
        "fullAssets": sum(1 for item in assets if item.get("kind") == "full"),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
