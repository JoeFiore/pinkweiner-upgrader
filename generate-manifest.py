#!/usr/bin/env python3
"""Generate mods-manifest.json for the Pinkweiner pack (one-click sync).

Reads:
  - tool-manifest.json  (113 Modrinth mods: filename, sha1, size, url)
  - modrinth.index.json (sha512 for those mods)
  - overrides/mods/*.jar (the 10 jars not on Modrinth; hashed locally)
  - ../GoldenAppleTree-1.2.6-26.3-Fabric.jar (hashed locally)

Writes:
  - mods-manifest.json  (pack_version 1.1.0: 113 Modrinth + 10 overrides
                         + Golden Apple Tree + a Golden Crops placeholder)
  - v2-upload-list.txt  (files Joseph must attach to the v2 GitHub release)

Override jars are fetched from the v2 release, so their URLs 404 until the
release exists. That is safe for existing players: the syncer only downloads
a file it does not already have with a matching hash.

Golden Crops is still being built: it stays a placeholder entry (no url) until
the jar exists. To fill it in, add the built jar's filename, size, sha1,
sha512 and its v2 release URL, then re-run this script (or edit the JSON).
"""
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PACK_VERSION = "1.1.0"
MINECRAFT = "26.3"
LOADER = "0.20.1"
RELEASE_TAG = "v2"
RELEASE_BASE = f"https://github.com/JoeFiore/pinkweiner-upgrader/releases/download/{RELEASE_TAG}"
GAT_JAR = os.path.join(os.path.dirname(HERE), "GoldenAppleTree-1.2.6-26.3-Fabric.jar")
GAT_URL = "https://cdn.modrinth.com/data/TevJ7APg/versions/kzZH4tTe/GoldenAppleTree-1.2.6-26.3-Fabric.jar"


def hashes(path):
    data = open(path, "rb").read()
    return len(data), hashlib.sha1(data).hexdigest(), hashlib.sha512(data).hexdigest()


def main():
    tool = json.load(open(os.path.join(HERE, "tool-manifest.json")))
    index = json.load(open(os.path.join(HERE, "modrinth.index.json")))
    sha512_by_name = {f["path"].split("/")[-1]: f["hashes"]["sha512"] for f in index["files"]}

    files = []
    # 1. The Modrinth mods (113). tool-manifest.json is the source of truth
    # for filename/size/sha1/url; the mrpack index adds sha512.
    for m in tool["mods"]:
        name = m["filename"]
        if name == "GoldenAppleTree-1.2.6-26.3-Fabric.jar":
            continue  # added separately below (not in the mrpack index)
        files.append({
            "filename": name,
            "size": m["size"],
            "sha1": m["sha1"],
            "sha512": sha512_by_name[name],
            "url": m["url"],
        })

    # 2. The override jars (10): hashed from the local copies, served from
    # the v2 GitHub release.
    mods_dir = os.path.join(HERE, "overrides", "mods")
    uploads = []
    for name in sorted(os.listdir(mods_dir)):
        if not name.endswith(".jar"):
            continue
        size, sha1, sha512 = hashes(os.path.join(mods_dir, name))
        files.append({
            "filename": name,
            "size": size,
            "sha1": sha1,
            "sha512": sha512,
            "url": f"{RELEASE_BASE}/{name}",
        })
        uploads.append(name)

    # 3. Golden Apple Tree (Modrinth, new in 1.1.0).
    size, sha1, sha512 = hashes(GAT_JAR)
    files.append({
        "filename": "GoldenAppleTree-1.2.6-26.3-Fabric.jar",
        "size": size,
        "sha1": sha1,
        "sha512": sha512,
        "url": GAT_URL,
    })

    files.sort(key=lambda f: f["filename"].lower())

    manifest = {
        "pack_version": PACK_VERSION,
        "minecraft": MINECRAFT,
        "loader": LOADER,
        "files": files,
        "placeholders": [
            {
                "name": "Golden Crops",
                "note": "Custom Pinkweiner mod, still being built. When the jar is ready, "
                        "add a files[] entry with its filename, size, sha1, sha512 and "
                        "v2 release URL, then remove this placeholder."
            }
        ],
    }
    with open(os.path.join(HERE, "mods-manifest.json"), "w") as fh:
        json.dump(manifest, fh, indent=1)
        fh.write("\n")

    with open(os.path.join(HERE, "v2-upload-list.txt"), "w") as fh:
        fh.write("Files to attach to the v2 release of JoeFiore/pinkweiner-upgrader\n")
        fh.write("=================================================================\n\n")
        fh.write("Override jars (from mc263-upgrade/overrides/mods/):\n")
        for name in uploads:
            fh.write(f"  {name}\n")
        fh.write("\nStill to come (do NOT create the release until these exist):\n")
        fh.write("  golden-crops-1.0.0.jar  (exact filename TBD — the Golden Crops\n")
        fh.write("                           builder agent will confirm it)\n")
        fh.write("  Pinkweiner.Upgrader.for.Windows.exe  (built from the v2 source)\n")
        fh.write("  Pinkweiner.Upgrader.for.Mac.zip.with.launcher.zip\n")
        fh.write("                           (v2 Mac binary + Pinkweiner-Upgrader.command)\n")

    print(f"mods-manifest.json: {len(files)} files (pack {PACK_VERSION})")
    print(f"v2-upload-list.txt: {len(uploads)} override jars listed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
