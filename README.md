# Pinkweiner Upgrader

Upgrades a Prism Launcher instance from Minecraft 26.2 to 26.3 for the Pinkweiner server, keeping the player's configs, keybinds, options, resource packs, shader packs, schematics, and waypoints. The old instance is never touched — a new `26.3NEW` instance is created.

## How it works

- Finds Prism Launcher automatically (Windows `%APPDATA%\PrismLauncher`, macOS `~/Library/Application Support/PrismLauncher`), with a `--prism-dir` override.
- Asks the player to **link their old instance** — the 26.2 instance they play on — and copies their settings from it (or does a fresh install with no old instance).
- Creates the new instance from the embedded `mmc-pack.json` (Minecraft 26.3, Fabric Loader 0.20.1).
- Extracts the built-in jars (custom mods + patched jars, not on Modrinth) and downloads the 113 Modrinth mods (8 at a time, SHA1-verified, 3 retries, resume skips files that already match).
- Verifies the mods folder holds every expected file, then prints launch instructions.

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
- `tool-manifest.json` — the 113 Modrinth mods (filename, SHA1, size, download URL).
- `mmc-pack.json` — the Prism pack definition (Minecraft 26.3 + Fabric Loader 0.20.1), written verbatim into new instances.
- `overrides/` — files embedded into the executable (see above).
