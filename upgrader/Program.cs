// Pinkweiner 26.3 Upgrader
// Two jobs in one tool:
//   1. Upgrade: creates a new Prism Launcher instance for Minecraft 26.3 from
//      a 26.2 instance (or a fresh install), keeping configs and packs.
//   2. Sync: one-click mod update for an existing 26.3 instance. Fetches the
//      pack manifest from the repo, downloads new/changed mods, and removes
//      managed mods that left the pack. Mods the player added themselves are
//      never touched.
//
// Usage: run with no arguments and answer the prompts.
//   --sync                 Go straight to mod sync (skip the mode menu).
//   --upgrade              Go straight to the 26.2 -> 26.3 upgrade.
//   --prism-dir <path>     Use a different Prism Launcher data folder.
//   --manifest <path>      Use a local mod manifest file instead of the
//                          built-in one (upgrade) or the online one (sync).
//   --manifest-url <url>   Sync only: fetch the pack manifest from this URL
//                          instead of the repo (testing).

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PinkweinerUpgrader;

internal static class Program
{
    private const string ToolVersion = "1.1.0";
    private const string DefaultNewName = "26.3NEW";
    private const string DefaultSourceName = "26.2NEW";
    private const int MaxConcurrentDownloads = 8;
    private const int MaxDownloadAttempts = 3;
    private const string ShoulderSurfingJar = "ShoulderSurfing-Fabric-26.3-5.3.1-patched2.jar";

    // The pack manifest lives in the public repo, so updating the pack is a
    // commit -- no new release and no new upgrader download needed. Players
    // just re-run the upgrader and pick Sync.
    private const string SyncManifestUrl =
        "https://raw.githubusercontent.com/JoeFiore/pinkweiner-upgrader/main/mods-manifest.json";

    // Written into the instance's minecraft/ folder (never inside mods/).
    // Records which jars the upgrader manages, so sync only ever deletes
    // jars it installed -- never mods the player added themselves.
    private const string StateFileName = "pinkweiner-managed.json";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object ConsoleLock = new();

    private sealed record ModEntry(string Filename, string Sha1, long Size, string Url, string? Sha512);

    private sealed record PackManifest(
        string PackVersion, string Minecraft, string Loader,
        List<ModEntry> Files, List<string> PlaceholderNotes);

    // Thrown when the user quits at a prompt (or stdin ends). Unwinds cleanly.
    private sealed class QuitException : Exception;

    internal static async Task<int> Main(string[] args)
    {
        int exitCode = 0;
        try
        {
            exitCode = await RunAsync(args);
        }
        catch (QuitException)
        {
            Console.WriteLine();
            Console.WriteLine("Okay, quitting. Nothing was changed.");
        }
        catch (Exception ex)
        {
            // Friendly message only -- never a stack trace.
            Console.WriteLine();
            Console.WriteLine("ERROR: " + ex.Message);
            Console.WriteLine("If this keeps happening, send Joseph the message above.");
            exitCode = 1;
        }

        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        try { Console.ReadLine(); } catch { /* stdin closed -- just exit */ }
        return exitCode;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pinkweiner-Upgrader/" + ToolVersion);
        return client;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? prismDirArg = GetArg(args, "--prism-dir");
        string? manifestArg = GetArg(args, "--manifest");
        string? manifestUrlArg = GetArg(args, "--manifest-url");

        Console.WriteLine("========================================");
        Console.WriteLine($"  Pinkweiner 26.3 Upgrader  (v{ToolVersion})");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // 1. Find Prism's data dir.
        string? prismDir = FindPrismDir(prismDirArg);
        if (prismDir is null)
        {
            return 0; // user quit at the path prompt
        }
        string instancesDir = Path.Combine(prismDir, "instances");

        // 2. Pick a mode: upgrade (first-time setup) or sync (update mods).
        bool syncMode = SelectMode(args, instancesDir);
        if (syncMode)
        {
            return await RunSyncAsync(instancesDir, manifestArg, manifestUrlArg);
        }
        return await RunUpgradeAsync(args, instancesDir, manifestArg);
    }

    private static bool SelectMode(string[] args, string instancesDir)
    {
        if (HasFlag(args, "--sync"))
        {
            return true;
        }
        if (HasFlag(args, "--upgrade"))
        {
            return false;
        }

        bool has263 = ListInstances(instancesDir).Any(name =>
            ReadInstanceVersions(Path.Combine(instancesDir, name)).Minecraft == "26.3");

        Console.WriteLine("What do you want to do?");
        Console.WriteLine("  1. Set up a 26.3 instance (first time: upgrade from 26.2, or fresh install)");
        Console.WriteLine("  2. Update the mods in my 26.3 instance (one-click sync)");
        Console.WriteLine();
        Console.WriteLine("Sync checks the pack for new or changed mods and installs them.");
        Console.WriteLine("Run it whenever Joseph says the pack updated.");
        Console.WriteLine();
        int defaultChoice = has263 ? 2 : 1;
        while (true)
        {
            Console.Write($"Enter 1 or 2 [default {defaultChoice}]: ");
            string? input = Console.ReadLine() ?? throw new QuitException();
            input = input.Trim();
            if (input.Length == 0)
            {
                return defaultChoice == 2;
            }
            if (input == "1")
            {
                return false;
            }
            if (input == "2")
            {
                return true;
            }
            Console.WriteLine("Please enter 1 or 2.");
        }
    }

    // ================= upgrade (first-time setup) =================

    private static async Task<int> RunUpgradeAsync(string[] args, string instancesDir, string? manifestArg)
    {
        Console.WriteLine("Upgrades a Prism Launcher instance from Minecraft 26.2 to 26.3,");
        Console.WriteLine("keeping your configs, resource packs, and settings.");
        Console.WriteLine("Your old instance is never touched -- a new one is created.");
        Console.WriteLine();

        // 1. Pick the source instance (or fresh install).
        string? sourceName = SelectSource(instancesDir);
        bool fresh = sourceName is null;
        string? oldInstanceDir = fresh ? null : Path.Combine(instancesDir, sourceName!);
        Console.WriteLine();
        if (!fresh)
        {
            Console.WriteLine($"Linked: {sourceName} -- your configs will be copied from here.");
        }
        else
        {
            Console.WriteLine("Fresh install -- nothing will be copied from an old instance.");
        }
        Console.WriteLine();

        // 2. New instance name (handles collisions).
        (string newName, NewInstanceAction action) = ChooseNewName(instancesDir);
        string newInstanceDir = Path.Combine(instancesDir, newName);
        bool resume = action == NewInstanceAction.Resume;

        // 3. Warn if Prism or Minecraft is running.
        if (!RunningCheck())
        {
            return 0; // user quit
        }

        // 4. Ask about singleplayer worlds (upgrade only, and only if they exist).
        bool copySaves = false;
        if (!fresh)
        {
            string savesDir = Path.Combine(oldInstanceDir!, "minecraft", "saves");
            if (Directory.Exists(savesDir))
            {
                copySaves = PromptYesNo(
                    "Copy your singleplayer worlds too? (You can always copy the saves folder manually later.)",
                    defaultYes: false);
            }
        }

        // Load the mod manifest before creating anything, so a bad --manifest
        // fails before any work is done.
        List<ModEntry> manifest = LoadManifest(manifestArg);
        Console.WriteLine();
        Console.WriteLine($"Mod list loaded: {manifest.Count} mods to install.");
        Console.WriteLine();

        // 5. Create the new instance (deleting first if the user chose that).
        if (action == NewInstanceAction.DeleteAndRecreate)
        {
            Directory.Delete(newInstanceDir, recursive: true);
        }
        string newMcDir = Path.Combine(newInstanceDir, "minecraft");
        string newModsDir = Path.Combine(newMcDir, "mods");
        Directory.CreateDirectory(newModsDir);
        Console.WriteLine(resume
            ? $"Resuming instance \"{newName}\"..."
            : $"Creating instance \"{newName}\"...");

        bool serverListAdded = WriteInstanceFiles(newInstanceDir, newMcDir, oldInstanceDir, newName, fresh);

        // 6. Copy the user's files from the old instance (upgrade only).
        if (!fresh)
        {
            int copied = CopyUserFiles(oldInstanceDir!, newMcDir, copySaves);
            Console.WriteLine($"Copied {copied} file(s) from your old instance (configs, settings, packs).");
        }
        else if (serverListAdded)
        {
            Console.WriteLine("Fresh install: the Pinkweiner server list was added for you.");
        }
        else
        {
            Console.WriteLine("Fresh install: no built-in server list in this build; add the Pinkweiner server in Prism (Multiplayer -> Add Server).");
        }

        // 7. Extract the built-in jars (custom mods + patched jars, not on Modrinth).
        var embedded = GetEmbeddedMods();
        Console.WriteLine($"Extracting {embedded.Count} built-in mod(s)...");
        foreach (var (fileName, bytes) in embedded)
        {
            File.WriteAllBytes(Path.Combine(newModsDir, fileName), bytes);
            Console.WriteLine("  + " + fileName);
        }

        // 8. Download the manifest mods.
        double totalMb = manifest.Sum(m => m.Size) / 1_000_000.0;
        Console.WriteLine($"Downloading {manifest.Count} mods ({totalMb:F1} MB)...");
        var (downloaded, skipped, failed) = await DownloadModsAsync(manifest, newModsDir);
        Console.WriteLine();

        // 9. Record which jars this tool installed, so a later sync knows
        // what it manages. Only jars actually present on disk are recorded.
        var expected = manifest.Select(m => m.Filename)
            .Concat(embedded.Select(e => e.FileName))
            .ToList();
        SaveStateFile(
            Path.Combine(newMcDir, StateFileName),
            packVersion: null,
            BuildManagedSet(newModsDir, expected));

        // 10. Verify and summarize.
        int actualFiles = Directory.GetFiles(newModsDir).Length;
        int expectedFiles = manifest.Count + embedded.Count;
        Console.WriteLine($"Mods folder: {actualFiles} file(s) (expected {expectedFiles}).");
        Console.WriteLine($"Downloaded: {downloaded}, already present: {skipped}, failed: {failed.Count}");
        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: these mods failed to download:");
            foreach (string f in failed)
            {
                Console.WriteLine("  - " + f);
            }
            Console.WriteLine("Re-run the tool to retry them (it will skip the ones that already downloaded).");
        }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine(failed.Count == 0 ? "All done!" : "Finished with some failed downloads.");
        Console.WriteLine("========================================");
        if (!fresh)
        {
            Console.WriteLine($"Your old instance \"{sourceName}\" was not touched.");
        }
        Console.WriteLine();
        Console.WriteLine($"  1. Open Prism Launcher.");
        Console.WriteLine($"  2. Launch \"{newName}\". The first launch downloads Java 25 and");
        Console.WriteLine($"     the game files automatically, so it may take a few minutes.");
        Console.WriteLine($"  3. Join the Pinkweiner server from the server list.");
        Console.WriteLine();
        Console.WriteLine("If you get a Network Protocol Error when joining, delete");
        Console.WriteLine($"  {ShoulderSurfingJar}");
        Console.WriteLine("from the new instance's mods folder and relaunch.");
        if (!fresh)
        {
            Console.WriteLine();
            Console.WriteLine("Once the new instance works, you can delete the old one from");
            Console.WriteLine("Prism if you want the disk space back.");
        }

        return failed.Count > 0 ? 1 : 0;
    }

    private enum NewInstanceAction
    {
        Create,
        DeleteAndRecreate,
        Resume,
    }

    // ================= sync (one-click mod update) =================

    private static async Task<int> RunSyncAsync(string instancesDir, string? manifestArg, string? manifestUrlArg)
    {
        Console.WriteLine("One-click mod sync: checks the Pinkweiner pack for new or changed");
        Console.WriteLine("mods and installs them into your existing 26.3 instance. Mods you");
        Console.WriteLine("added yourself are never touched or deleted.");
        Console.WriteLine();

        // 1. Pick the target instance.
        string targetName = SelectSyncTarget(instancesDir);
        string targetDir = Path.Combine(instancesDir, targetName);
        string targetMcDir = Path.Combine(targetDir, "minecraft");
        string targetModsDir = Path.Combine(targetMcDir, "mods");
        Console.WriteLine();
        Console.WriteLine($"Syncing instance: {targetName}");
        Console.WriteLine();

        // 2. Warn if Prism or Minecraft is running.
        if (!RunningCheck())
        {
            return 0; // user quit
        }

        // 3. Load the pack manifest (local file for testing, else the repo).
        PackManifest pack;
        if (manifestArg is not null)
        {
            if (!File.Exists(manifestArg))
            {
                throw new InvalidOperationException($"Manifest file not found: {manifestArg}");
            }
            pack = ParsePackManifest(File.ReadAllText(manifestArg));
            Console.WriteLine($"Pack manifest loaded from file: {manifestArg}");
        }
        else
        {
            string url = manifestUrlArg ?? SyncManifestUrl;
            Console.WriteLine("Fetching the latest pack list...");
            string json;
            try
            {
                json = await Http.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not download the pack list. Check your internet connection and try again. " +
                    $"({ex.Message})");
            }
            pack = ParsePackManifest(json);
        }
        Console.WriteLine($"Pack version {pack.PackVersion}: {pack.Files.Count} mods " +
                          $"(Minecraft {pack.Minecraft}, Fabric Loader {pack.Loader}).");
        foreach (string note in pack.PlaceholderNotes)
        {
            Console.WriteLine("  (coming soon: " + note + ")");
        }
        Console.WriteLine();

        // 4. Refuse to sync into an instance whose game/loader version does
        // not match the pack. Nothing has changed yet at this point.
        (string? mcVersion, string? loaderVersion) = ReadInstanceVersions(targetDir);
        if (mcVersion is null || loaderVersion is null)
        {
            Console.WriteLine("ERROR: could not read this instance's Minecraft version");
            Console.WriteLine("(mmc-pack.json is missing or unreadable). Sync refused --");
            Console.WriteLine("nothing was changed. Pick your 26.3 instance, or run the");
            Console.WriteLine("upgrade to set one up.");
            return 1;
        }
        if (!mcVersion.Equals(pack.Minecraft, StringComparison.Ordinal) ||
            !loaderVersion.Equals(pack.Loader, StringComparison.Ordinal))
        {
            Console.WriteLine($"ERROR: this instance runs Minecraft {mcVersion} / Fabric Loader {loaderVersion},");
            Console.WriteLine($"but the pack needs Minecraft {pack.Minecraft} / Fabric Loader {pack.Loader}.");
            Console.WriteLine("Sync refused -- nothing was changed. Pick your 26.3 instance,");
            Console.WriteLine("or run the upgrade to set one up.");
            return 1;
        }
        Console.WriteLine($"Instance version matches: Minecraft {mcVersion}, Fabric Loader {loaderVersion}.");
        Console.WriteLine();

        Directory.CreateDirectory(targetModsDir);

        // 5. Load the managed-jars state file. First sync ever: no state file
        // yet, so the managed set starts empty and every pack jar found on
        // disk is adopted (verified by hash below). A corrupt state file is
        // treated as empty -- the safe direction, since removal only ever
        // touches jars the state file proves this tool manages.
        Dictionary<string, string> managed =
            LoadStateFile(Path.Combine(targetMcDir, StateFileName)).Managed;

        // 6. Compare the mods folder against the pack.
        var manifestByName = pack.Files.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);
        var toDownload = new List<ModEntry>();
        var alreadyThere = new List<string>();
        var existedBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in pack.Files)
        {
            string dest = Path.Combine(targetModsDir, mod.Filename);
            if (File.Exists(dest))
            {
                existedBefore.Add(mod.Filename);
                if (VerifyFile(dest, mod))
                {
                    alreadyThere.Add(mod.Filename);
                    continue;
                }
            }
            toDownload.Add(mod);
        }

        var toRemove = new List<string>();
        foreach (string name in managed.Keys)
        {
            if (!manifestByName.ContainsKey(name) &&
                File.Exists(Path.Combine(targetModsDir, name)))
            {
                toRemove.Add(name);
            }
        }
        toRemove.Sort(StringComparer.OrdinalIgnoreCase);

        var unmanaged = new List<string>();
        foreach (string file in Directory.GetFiles(targetModsDir))
        {
            string name = Path.GetFileName(file);
            if (!manifestByName.ContainsKey(name) && !managed.ContainsKey(name))
            {
                unmanaged.Add(name);
            }
        }
        unmanaged.Sort(StringComparer.OrdinalIgnoreCase);

        int toAdd = toDownload.Count(m => !existedBefore.Contains(m.Filename));
        int toUpdate = toDownload.Count - toAdd;
        Console.WriteLine($"Plan: {toAdd} new, {toUpdate} updated, {toRemove.Count} removed, " +
                          $"{alreadyThere.Count} already up to date, {unmanaged.Count} of yours left alone.");
        foreach (ModEntry mod in toDownload)
        {
            Console.WriteLine("  " + (existedBefore.Contains(mod.Filename) ? "~ " : "+ ") + mod.Filename);
        }
        foreach (string name in toRemove)
        {
            Console.WriteLine("  - " + name + " (no longer in the pack)");
        }
        if (toDownload.Count == 0 && toRemove.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Already up to date -- nothing to download or remove.");
        }
        Console.WriteLine();

        // 7. Download new/changed mods. A failed download never touches the
        // file already on disk (downloads go to a .download temp file and are
        // only moved into place after size + hash verification).
        var (downloaded, _, failed) = await DownloadModsAsync(toDownload, targetModsDir);
        Console.WriteLine();

        // 8. Remove managed jars that left the pack. Only jars recorded in
        // the state file are ever deleted.
        int removed = 0;
        var removeFailed = new List<string>();
        foreach (string name in toRemove)
        {
            try
            {
                File.Delete(Path.Combine(targetModsDir, name));
                Console.WriteLine("  - removed " + name);
                removed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! could not remove {name}: {ex.Message}");
                removeFailed.Add(name);
            }
        }

        // 9. Save the state file from ground truth: every pack jar actually
        // on disk afterwards, with its real hash. A failed update keeps the
        // old jar's hash, so the next sync retries it instead of forgetting it.
        SaveStateFile(
            Path.Combine(targetMcDir, StateFileName),
            pack.PackVersion,
            BuildManagedSet(targetModsDir, pack.Files.Select(m => m.Filename).ToList()));

        // 10. Summary.
        int added = toDownload.Count(m => !existedBefore.Contains(m.Filename) && !failed.Contains(m.Filename));
        int updated = downloaded - added;
        Console.WriteLine("========================================");
        Console.WriteLine(failed.Count == 0 && removeFailed.Count == 0
            ? "Sync complete!"
            : "Sync finished with some problems.");
        Console.WriteLine("========================================");
        Console.WriteLine($"Added: {added}, updated: {updated}, removed: {removed}, " +
                          $"already up to date: {alreadyThere.Count}.");
        if (unmanaged.Count > 0)
        {
            Console.WriteLine($"Left alone ({unmanaged.Count} of your own mods -- never touched):");
            foreach (string name in unmanaged.Take(10))
            {
                Console.WriteLine("  = " + name);
            }
            if (unmanaged.Count > 10)
            {
                Console.WriteLine($"  ... and {unmanaged.Count - 10} more.");
            }
        }
        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: these mods failed to download (your old files, if any, were kept):");
            foreach (string f in failed)
            {
                Console.WriteLine("  - " + f);
            }
            Console.WriteLine("Re-run the sync to retry them.");
        }
        if (removeFailed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: these old mods could not be removed:");
            foreach (string f in removeFailed)
            {
                Console.WriteLine("  - " + f);
            }
        }
        Console.WriteLine();
        Console.WriteLine("You can launch the instance in Prism now.");

        return failed.Count > 0 || removeFailed.Count > 0 ? 1 : 0;
    }

    // Picks the instance to sync into. Defaults to 26.3NEW when present,
    // else the first instance running Minecraft 26.3, else the first one.
    private static string SelectSyncTarget(string instancesDir)
    {
        List<string> instances = ListInstances(instancesDir);
        if (instances.Count == 0)
        {
            throw new InvalidOperationException(
                "No Prism instances were found. Run the upgrade first to set up a 26.3 instance.");
        }

        Console.WriteLine("Pick the instance to update:");
        var versions = new Dictionary<string, (string? Mc, string? Loader)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < instances.Count; i++)
        {
            var v = ReadInstanceVersions(Path.Combine(instancesDir, instances[i]));
            versions[instances[i]] = v;
            string hint = v.Minecraft is null ? "" : $" (Minecraft {v.Minecraft})";
            Console.WriteLine($"  {i + 1}. {instances[i]}{hint}");
        }

        int defaultChoice = 0;
        int idx263New = instances.FindIndex(n => n.Equals(DefaultNewName, StringComparison.OrdinalIgnoreCase));
        if (idx263New >= 0)
        {
            defaultChoice = idx263New + 1;
        }
        else
        {
            int idx263 = instances.FindIndex(n =>
                versions[n].Mc == "26.3");
            defaultChoice = idx263 >= 0 ? idx263 + 1 : 1;
        }

        while (true)
        {
            Console.Write($"Enter a number [default {defaultChoice}]: ");
            string? input = Console.ReadLine() ?? throw new QuitException();
            input = input.Trim();
            if (input.Length == 0)
            {
                return instances[defaultChoice - 1];
            }
            if (int.TryParse(input, out int n) && n >= 1 && n <= instances.Count)
            {
                return instances[n - 1];
            }
            Console.WriteLine($"Please enter a number from 1 to {instances.Count}.");
        }
    }

    // Reads the Minecraft + Fabric Loader versions out of an instance's
    // mmc-pack.json. Returns (null, null) when the file is missing or broken.
    private static (string? Minecraft, string? Loader) ReadInstanceVersions(string instanceDir)
    {
        try
        {
            string path = Path.Combine(instanceDir, "mmc-pack.json");
            if (!File.Exists(path))
            {
                return (null, null);
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string? mc = null;
            string? loader = null;
            foreach (JsonElement el in doc.RootElement.GetProperty("components").EnumerateArray())
            {
                string? uid = el.TryGetProperty("uid", out JsonElement u) ? u.GetString() : null;
                string? version = el.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
                if (uid == "net.minecraft")
                {
                    mc = version;
                }
                else if (uid == "net.fabricmc.fabric-loader")
                {
                    loader = version;
                }
            }
            return (mc, loader);
        }
        catch
        {
            return (null, null);
        }
    }

    // ----- managed-jars state file -----

    // Returns the recorded pack version (null when the file is missing or has
    // none) and filename -> sha1 of the jars this tool manages. A missing or
    // corrupt file means "nothing managed yet" -- never an error.
    private static (string? PackVersion, Dictionary<string, string> Managed) LoadStateFile(string path)
    {
        var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return (null, managed);
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string? version = doc.RootElement.TryGetProperty("pack_version", out JsonElement v)
                ? v.GetString()
                : null;
            if (doc.RootElement.TryGetProperty("managed", out JsonElement m))
            {
                foreach (JsonProperty prop in m.EnumerateObject())
                {
                    string? hash = prop.Value.GetString();
                    if (hash is not null)
                    {
                        managed[prop.Name] = hash;
                    }
                }
            }
            return (version, managed);
        }
        catch
        {
            Console.WriteLine($"  (note: {StateFileName} was unreadable, starting fresh -- " +
                              "your own mods are still safe)");
            return (null, managed);
        }
    }

    private static void SaveStateFile(string path, string? packVersion, Dictionary<string, string> managed)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                if (packVersion is not null)
                {
                    writer.WriteString("pack_version", packVersion);
                }
                writer.WriteStartObject("managed");
                foreach (var kv in managed.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteString(kv.Key, kv.Value);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            File.WriteAllBytes(path, ms.ToArray());
        }
        catch (Exception ex)
        {
            // A state file we cannot write only costs future removals, never
            // correctness of the mods themselves. Say so and carry on.
            Console.WriteLine($"  (note: could not save {StateFileName}: {ex.Message})");
        }
    }

    // Ground truth for the state file: for each expected filename that is
    // actually on disk, its real SHA1. Files that are missing (failed
    // downloads) are left out.
    private static Dictionary<string, string> BuildManagedSet(string modsDir, List<string> expectedFilenames)
    {
        var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in expectedFilenames)
        {
            string path = Path.Combine(modsDir, name);
            if (File.Exists(path))
            {
                try
                {
                    managed[name] = Sha1Hex(path);
                }
                catch
                {
                    // Unreadable file: leave it out of the state file.
                }
            }
        }
        return managed;
    }

    // ----- arguments -----

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ----- step 1: find Prism -----

    private static string DefaultPrismDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrismLauncher");
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "PrismLauncher");
        }
        // Linux default (Prism's own default); also handy for testing.
        return Path.Combine(home, ".local", "share", "PrismLauncher");
    }

    private static bool IsValidPrismDir(string path) =>
        Directory.Exists(Path.Combine(path, "instances"));

    private static string? FindPrismDir(string? overrideDir)
    {
        if (overrideDir is not null)
        {
            if (IsValidPrismDir(overrideDir))
            {
                Console.WriteLine($"Using Prism Launcher at: {overrideDir}");
                return overrideDir;
            }
            Console.WriteLine($"WARNING: --prism-dir '{overrideDir}' does not contain an 'instances' folder.");
        }

        string def = DefaultPrismDir();
        if (IsValidPrismDir(def))
        {
            Console.WriteLine($"Found Prism Launcher at: {def}");
            return def;
        }

        Console.WriteLine("Could not find Prism Launcher at the usual place.");
        Console.WriteLine("Prism usually lives here:");
        Console.WriteLine("  Windows: %APPDATA%\\PrismLauncher");
        Console.WriteLine("  Mac: ~/Library/Application Support/PrismLauncher");
        Console.WriteLine("  Linux: ~/.local/share/PrismLauncher");
        Console.WriteLine();
        while (true)
        {
            Console.WriteLine("Type or paste the path to your Prism Launcher data folder");
            Console.WriteLine("(the folder that contains the 'instances' folder), or press Enter to quit.");
            Console.Write("> ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }
            input = input.Trim().Trim('"');
            if (IsValidPrismDir(input))
            {
                return input;
            }
            Console.WriteLine("That folder does not contain an 'instances' folder. Try again.");
            Console.WriteLine();
        }
    }

    // ----- step 2: pick the source instance -----

    private static List<string> ListInstances(string instancesDir)
    {
        var names = new List<string>();
        foreach (string dir in Directory.GetDirectories(instancesDir))
        {
            if (File.Exists(Path.Combine(dir, "instance.cfg")))
            {
                names.Add(Path.GetFileName(dir));
            }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // Returns the source instance name, or null for a fresh install.
    private static string? SelectSource(string instancesDir)
    {
        List<string> instances = ListInstances(instancesDir);

        Console.WriteLine("Link your old instance:");
        Console.WriteLine("Pick the instance you currently play on (your 26.2 one). The upgrader");
        Console.WriteLine("copies your configs, keybinds, options, and packs from it into the new");
        Console.WriteLine("26.3 instance. Your old instance is never changed or deleted.");
        Console.WriteLine();
        Console.WriteLine("This step is required: pick an instance below, or choose 0 for a fresh");
        Console.WriteLine("install with no copied settings.");
        Console.WriteLine();

        if (instances.Count == 0)
        {
            Console.WriteLine("No instances were found in this Prism folder.");
            Console.WriteLine("You can still do a fresh install (no old instance to copy from).");
            bool goFresh = PromptYesNo("Do a fresh install instead?", defaultYes: true);
            return goFresh ? null : throw new QuitException();
        }

        for (int i = 0; i < instances.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {instances[i]}");
        }
        Console.WriteLine("  0. Fresh install (no old instance)");

        int defaultChoice = 0; // 0 means "no default" unless 26.2NEW exists
        int idx262 = instances.FindIndex(n => n.Equals(DefaultSourceName, StringComparison.OrdinalIgnoreCase));
        if (idx262 >= 0)
        {
            defaultChoice = idx262 + 1;
        }
        else if (instances.Count > 0)
        {
            defaultChoice = 1;
        }

        while (true)
        {
            Console.Write(defaultChoice > 0
                ? $"Enter a number [default {defaultChoice}]: "
                : "Enter a number: ");
            string? input = Console.ReadLine() ?? throw new QuitException();
            input = input.Trim();
            if (input.Length == 0 && defaultChoice > 0)
            {
                return instances[defaultChoice - 1];
            }
            if (int.TryParse(input, out int n))
            {
                if (n == 0)
                {
                    return null;
                }
                if (n >= 1 && n <= instances.Count)
                {
                    return instances[n - 1];
                }
            }
            Console.WriteLine($"Please enter a number from 0 to {instances.Count}.");
        }
    }

    // ----- step 3: new instance name -----

    private static (string Name, NewInstanceAction Action) ChooseNewName(string instancesDir)
    {
        Console.Write($"New instance name [{DefaultNewName}]: ");
        string name = (Console.ReadLine() ?? throw new QuitException()).Trim();
        if (name.Length == 0)
        {
            name = DefaultNewName;
        }

        while (true)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length == 0)
            {
                Console.WriteLine("That name cannot be used as a folder name. Try another one.");
                Console.Write("New instance name: ");
                name = (Console.ReadLine() ?? throw new QuitException()).Trim();
                continue;
            }

            string target = Path.Combine(instancesDir, name);
            if (!Directory.Exists(target))
            {
                return (name, NewInstanceAction.Create);
            }

            Console.WriteLine();
            Console.WriteLine($"An instance named \"{name}\" already exists.");
            Console.WriteLine("  1. Delete it and start fresh");
            Console.WriteLine("  2. Resume it (keeps mods already downloaded, re-checks everything)");
            Console.WriteLine("  3. Use a different name");
            Console.Write("Choose 1-3 [2]: ");
            string choice = (Console.ReadLine() ?? throw new QuitException()).Trim();
            if (choice.Length == 0)
            {
                choice = "2";
            }
            switch (choice)
            {
                case "1":
                    return (name, NewInstanceAction.DeleteAndRecreate);
                case "2":
                    return (name, NewInstanceAction.Resume);
                case "3":
                    Console.Write("New instance name: ");
                    name = (Console.ReadLine() ?? throw new QuitException()).Trim();
                    if (name.Length == 0)
                    {
                        name = DefaultNewName;
                    }
                    break;
                default:
                    Console.WriteLine("Please enter 1, 2, or 3.");
                    break;
            }
        }
    }

    // ----- step 4: running check -----

    private static bool RunningCheck()
    {
        string[] procNames = ["PrismLauncher", "javaw", "java"];
        while (true)
        {
            var found = new List<string>();
            foreach (string procName in procNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(procName);
                    if (procs.Length > 0)
                    {
                        found.Add(procName);
                    }
                    foreach (Process p in procs)
                    {
                        p.Dispose();
                    }
                }
                catch
                {
                    // If we cannot check a process name, ignore it.
                }
            }

            if (found.Count == 0)
            {
                Console.WriteLine("Prism and Minecraft are not running. Good.");
                return true;
            }

            Console.WriteLine();
            Console.WriteLine("WARNING: it looks like Prism Launcher and/or Minecraft is still running:");
            foreach (string f in found)
            {
                Console.WriteLine("  - " + f);
            }
            Console.WriteLine("Please close them, then press Enter to continue (or type Q to quit).");
            Console.Write("> ");
            string? input = Console.ReadLine();
            if (input is null)
            {
                throw new QuitException();
            }
            if (input.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // Loop and re-check.
        }
    }

    // ----- prompts -----

    private static bool PromptYesNo(string question, bool defaultYes)
    {
        string hint = defaultYes ? "[Y/n]" : "[y/N]";
        while (true)
        {
            Console.Write($"{question} {hint} ");
            string? input = Console.ReadLine() ?? throw new QuitException();
            input = input.Trim().ToLowerInvariant();
            if (input.Length == 0)
            {
                return defaultYes;
            }
            if (input is "y" or "yes")
            {
                return true;
            }
            if (input is "n" or "no")
            {
                return false;
            }
            Console.WriteLine("Please answer y or n.");
        }
    }

    // ----- manifests -----

    // The built-in upgrade manifest (tool-manifest.json): {"mods": [...]}.
    private static List<ModEntry> LoadManifest(string? overridePath)
    {
        string json;
        if (overridePath is not null)
        {
            if (!File.Exists(overridePath))
            {
                throw new InvalidOperationException($"Manifest file not found: {overridePath}");
            }
            json = File.ReadAllText(overridePath);
        }
        else
        {
            json = Encoding.UTF8.GetString(GetEmbeddedBytes("tool-manifest.json"));
        }

        var mods = new List<ModEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (JsonElement el in doc.RootElement.GetProperty("mods").EnumerateArray())
        {
            mods.Add(ParseModEntry(el));
        }
        if (mods.Count == 0)
        {
            throw new InvalidOperationException("The mod manifest is empty.");
        }
        return mods;
    }

    // The online sync manifest (mods-manifest.json): pack_version, minecraft,
    // loader, files[], placeholders[].
    private static PackManifest ParsePackManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string packVersion = root.TryGetProperty("pack_version", out JsonElement pv)
            ? pv.GetString() ?? "?"
            : "?";
        string minecraft = root.GetProperty("minecraft").GetString()
            ?? throw new InvalidOperationException("Pack manifest is missing 'minecraft'.");
        string loader = root.GetProperty("loader").GetString()
            ?? throw new InvalidOperationException("Pack manifest is missing 'loader'.");

        var files = new List<ModEntry>();
        foreach (JsonElement el in root.GetProperty("files").EnumerateArray())
        {
            files.Add(ParseModEntry(el));
        }
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The pack manifest has no files.");
        }

        var notes = new List<string>();
        if (root.TryGetProperty("placeholders", out JsonElement ph))
        {
            foreach (JsonElement el in ph.EnumerateArray())
            {
                string name = el.TryGetProperty("name", out JsonElement n)
                    ? n.GetString() ?? "?"
                    : "?";
                notes.Add(name);
            }
        }
        return new PackManifest(packVersion, minecraft, loader, files, notes);
    }

    private static ModEntry ParseModEntry(JsonElement el)
    {
        string filename = el.GetProperty("filename").GetString()
            ?? throw new InvalidOperationException("Manifest entry is missing a filename.");
        string sha1 = el.GetProperty("sha1").GetString()
            ?? throw new InvalidOperationException($"Manifest entry '{filename}' is missing a sha1.");
        long size = el.GetProperty("size").GetInt64();
        string url = el.GetProperty("url").GetString()
            ?? throw new InvalidOperationException($"Manifest entry '{filename}' is missing a url.");
        string? sha512 = el.TryGetProperty("sha512", out JsonElement h) ? h.GetString() : null;
        return new ModEntry(filename, sha1, size, url, sha512);
    }

    // ----- embedded resources -----

    private static byte[] GetEmbeddedBytes(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException($"Built-in file is missing from this download: {name}");
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // Same as GetEmbeddedBytes, but returns null instead of throwing when
    // the resource was not embedded (used for optional build-time files).
    private static byte[]? TryGetEmbeddedBytes(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static List<(string FileName, byte[] Bytes)> GetEmbeddedMods()
    {
        var result = new List<(string FileName, byte[] Bytes)>();
        foreach (string resName in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!resName.StartsWith("mods/", StringComparison.Ordinal))
            {
                continue;
            }
            string fileName = resName["mods/".Length..];
            result.Add((fileName, GetEmbeddedBytes(resName)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
        return result;
    }

    // ----- instance files -----

    // Returns true when a built-in server list was written (fresh installs only).
    private static bool WriteInstanceFiles(
        string newInstanceDir, string newMcDir, string? oldInstanceDir, string newName, bool fresh)
    {
        // mmc-pack.json: embedded, written verbatim.
        File.WriteAllBytes(
            Path.Combine(newInstanceDir, "mmc-pack.json"),
            GetEmbeddedBytes("mmc-pack.json"));

        // instance.cfg: upgrade copies the old one (changing only the name line);
        // fresh installs get a minimal one.
        string newCfgPath = Path.Combine(newInstanceDir, "instance.cfg");
        if (!fresh)
        {
            string oldCfgPath = Path.Combine(oldInstanceDir!, "instance.cfg");
            string text = File.ReadAllText(oldCfgPath);
            File.WriteAllText(newCfgPath, ReplaceNameLine(text, newName));
        }
        else
        {
            File.WriteAllText(newCfgPath, FreshInstanceCfg(newName));
        }

        // servers.dat: only on fresh installs. On upgrades, the user's own
        // servers.dat is copied over by CopyUserFiles instead.
        // The file is optional at build time (it is not in the public repo):
        // without it, fresh installs just don't get a pre-added server.
        if (fresh)
        {
            byte[]? serverList = TryGetEmbeddedBytes("servers.dat");
            if (serverList is not null)
            {
                File.WriteAllBytes(Path.Combine(newMcDir, "servers.dat"), serverList);
                return true;
            }
            return false;
        }
        return false;
    }

    private static string FreshInstanceCfg(string name) =>
        "[General]\n" +
        $"name={name}\n" +
        "InstanceType=OneSix\n" +
        "ConfigVersion=1.3\n" +
        "MaxMemAlloc=4096\n" +
        "MinMemAlloc=512\n" +
        "JvmArgs=-XX:+UseZGC\n" +
        "AutomaticJava=true\n";

    // Changes ONLY the `name=` line, preserving every other line and the
    // file's original line endings.
    private static string ReplaceNameLine(string text, string newName)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("name=", StringComparison.Ordinal))
            {
                lines[i] = "name=" + newName + (line.EndsWith('\r') ? "\r" : "");
                return string.Join('\n', lines);
            }
        }

        // No name= line: insert one after [General] if present, else append.
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "[General]")
            {
                var list = lines.ToList();
                list.Insert(i + 1, "name=" + newName);
                return string.Join('\n', list);
            }
        }
        return text + (text.EndsWith('\n') ? "" : "\n") + "name=" + newName + "\n";
    }

    // ----- copying user files -----

    private static int CopyUserFiles(string oldInstanceDir, string newMcDir, bool copySaves)
    {
        string oldMcDir = Path.Combine(oldInstanceDir, "minecraft");
        int copied = 0;

        string[] foldersToCopy = ["config", "resourcepacks", "shaderpacks", "schematics", "xaero", "oneconfig"];
        foreach (string folder in foldersToCopy)
        {
            string src = Path.Combine(oldMcDir, folder);
            if (Directory.Exists(src))
            {
                copied += CopyDirectory(src, Path.Combine(newMcDir, folder));
            }
        }

        string[] filesToCopy = ["options.txt", "servers.dat", "hotbar.nbt"];
        foreach (string file in filesToCopy)
        {
            string src = Path.Combine(oldMcDir, file);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(newMcDir, file), overwrite: true);
                copied++;
            }
        }

        if (copySaves)
        {
            string src = Path.Combine(oldMcDir, "saves");
            if (Directory.Exists(src))
            {
                copied += CopyDirectory(src, Path.Combine(newMcDir, "saves"));
            }
        }

        // Deliberately NOT copied: mods/, logs/, crash-reports/, debug/,
        // screenshots/, .fabric/, servers.dat_old.
        return copied;
    }

    private static int CopyDirectory(string source, string dest)
    {
        int count = 0;
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            count++;
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            count += CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
        return count;
    }

    // ----- downloads -----

    private static async Task<(int Downloaded, int Skipped, List<string> Failed)> DownloadModsAsync(
        List<ModEntry> mods, string modsDir)
    {
        long totalBytes = mods.Sum(m => m.Size);
        var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
        int done = 0;
        int downloaded = 0;
        int skipped = 0;
        long accountedBytes = 0;
        var failed = new List<string>();
        object failedLock = new();

        var tasks = mods.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                string dest = Path.Combine(modsDir, mod.Filename);
                bool present = false;
                bool ok = false;

                // Resume support: skip files that already exist and verify.
                if (File.Exists(dest) && VerifyFile(dest, mod))
                {
                    present = true;
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    for (int attempt = 1; attempt <= MaxDownloadAttempts && !ok; attempt++)
                    {
                        ok = await TryDownloadAsync(mod, dest);
                        if (!ok && attempt < MaxDownloadAttempts)
                        {
                            await Task.Delay(1000 * attempt);
                        }
                    }
                    if (ok)
                    {
                        Interlocked.Increment(ref downloaded);
                    }
                    else
                    {
                        lock (failedLock)
                        {
                            failed.Add(mod.Filename);
                        }
                    }
                }

                int n = Interlocked.Increment(ref done);
                long acc = 0;
                if (present || ok)
                {
                    acc = Interlocked.Add(ref accountedBytes, mod.Size);
                }
                else
                {
                    acc = Interlocked.Read(ref accountedBytes);
                }

                string status = present ? " - already present" : (!ok ? " - FAILED" : "");
                lock (ConsoleLock)
                {
                    Console.WriteLine(
                        $"[{n}/{mods.Count}] {mod.Filename} " +
                        $"({acc / 1_000_000.0:F1} MB / {totalBytes / 1_000_000.0:F1} MB){status}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        failed.Sort(StringComparer.Ordinal);
        return (downloaded, skipped, failed);
    }

    private static async Task<bool> TryDownloadAsync(ModEntry mod, string dest)
    {
        string tmp = dest + ".download";
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(mod.Url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs);
            }
            // Verify before installing: a bad download never replaces the
            // file already on disk.
            if (!VerifyFile(tmp, mod))
            {
                File.Delete(tmp);
                return false;
            }
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
            return false;
        }
    }

    // Size + SHA1, plus SHA512 when the manifest entry carries one.
    private static bool VerifyFile(string path, ModEntry mod)
    {
        try
        {
            if (new FileInfo(path).Length != mod.Size)
            {
                return false;
            }
            if (!Sha1Hex(path).Equals(mod.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (mod.Sha512 is not null &&
                !Sha512Hex(path).Equals(mod.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Sha1Hex(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string Sha512Hex(string path)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha512.ComputeHash(stream)).ToLowerInvariant();
    }
}
