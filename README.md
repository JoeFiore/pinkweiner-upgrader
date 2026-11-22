# Pinkweiner Upgrader

Upgrades a Prism Launcher instance from Minecraft 26.2 to 26.3 for the Pinkweiner server, keeping the player's configs, keybinds, options, resource packs, shader packs, schematics, and waypoints. The old instance is never touched — a new `26.3NEW` instance is created.

It also does one-click mod sync: when the pack changes, players just re-run the upgrader and pick **Sync**, and it installs the new or changed mods into their existing 26.3 instance. No new download needed — the pack list is fetched from this repo.

## How it works

- Finds Prism Launcher automatically (Windows `%APPDATA%\PrismLauncher`, macOS `~/Library/Application Support/PrismLauncher`), with a `--prism-dir` override.
- Asks the player to **link their old instance** — the 26.2 instance they play on — and copies their settings from it (or does a fresh install with no old instance).
- Creates the new instance from the embedded `mmc-pack.json` (Minecraft 26.3, Fabric Loader 0.20.1).
- Extracts the built-in jars (custom mods + patched jars, not on Modrinth) and downloads the 114 Modrinth mods (8 at a time, SHA1+SHA512-verified, 3 retries, resume skips files that already match).
- Verifies the mods folder holds every expected file, then prints launch instructions.

## Updating the pack (one-click sync)

The pack list is `mods-manifest.json` in this repo. The upgrader fetches it from `raw.githubusercontent.com` every time a player runs Sync, so updating the pack is just a commit — no new release and no new upgrader download.

To change the pack:

1. Edit `mods-manifest.json` (or regenerate it with `python3 generate-manifest.py`, which rebuilds it from `tool-manifest.json`, `modrinth.index.json`, and `overrides/mods/`).
2. If you added a jar that isn't on Modrinth, upload it to the GitHub release the manifest points at (see `v2-upload-list.txt`) — the manifest URL for it is `https://github.com/JoeFiore/pinkweiner-upgrader/releases/download/<tag>/<filename>`.
3. Commit and push. Players re-run the upgrader, pick **Sync**, and get the new mods.

How sync decides what to do:

- Downloads mods that are missing or whose size/hash doesn't match the manifest. Every download is verified (size + SHA1 + SHA512) before it replaces anything; a bad download is refused and the old file is kept.
- Removes managed mods that left the pack. "Managed" means recorded in the instance's `minecraft/pinkweiner-managed.json` state file, which the upgrader writes on every upgrade and sync. Mods the player added themselves are never in that file, so they are never deleted.
- Refuses to sync into an instance whose Minecraft or Fabric Loader version doesn't match the manifest.

`mods-manifest.json` may also carry a `placeholders` list for mods that are coming soon (e.g. Golden Crops while it's being built). The upgrader prints them and skips them.

## Building

Requires the .NET 10 SDK. From the repo root:

```sh
# Windows
dotnet publish upgrader -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o dist-win

# macOS (Apple Silicon)
dotnet publish upgrader -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o dist-mac
```

The output is a single self-contained executable (~19 MB). No external NuGet packages are used.

## Files not in this repo

Two things are deliberately left out of the public repo:

- **`overrides/mods/*.jar`** — the 11 jars that aren't on Modrinth: the 9 custom Pinkweiner mods plus the patched ShoulderSurfing and Light Level Overlay jars. Drop them into `overrides/mods/` before building and they get embedded automatically. Without them the tool still builds and runs, but the new instance will be missing those 11 mods.
- **`overrides/servers.dat`** — the Pinkweiner server list, written into fresh installs only. Copy it from any 26.3NEW instance's `minecraft/servers.dat` into `overrides/` before building. Without it the build still succeeds; fresh installs just don't get a pre-added server.

## Layout

- `upgrader/` — the C# console app (`Program.cs`, `PinkweinerUpgrader.csproj`).
- `tool-manifest.json` — the 114 Modrinth mods (filename, SHA1, size, download URL), embedded into the upgrader for first-time installs.
- `mods-manifest.json` — the full pack list for one-click sync (pack version, Minecraft + loader versions, all 124 files with SHA1+SHA512, plus placeholders). Fetched from this repo at sync time.
- `generate-manifest.py` — rebuilds `mods-manifest.json` from `tool-manifest.json`, `modrinth.index.json`, and `overrides/mods/`.
- `v2-upload-list.txt` — the jars that must be attached to the v2 GitHub release for sync to download them.
- `mmc-pack.json` — the Prism pack definition (Minecraft 26.3 + Fabric Loader 0.20.1), written verbatim into new instances.
- `overrides/` — files embedded into the executable (see above).
