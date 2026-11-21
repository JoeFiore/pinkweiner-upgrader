// Pinkweiner 26.3 Upgrader
// Upgrades a friend's Prism Launcher Minecraft instance from 26.2 to 26.3,
// keeping their configs, resource packs, and settings. The old instance is
// never modified -- everything is read from it and written to a new instance.
//
// Usage: run with no arguments and answer the prompts.
//   --prism-dir <path>   Use a different Prism Launcher data folder.
//   --manifest <path>    Use a different mod manifest (testing).

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PinkweinerUpgrader;

internal static class Program
{
    private const string DefaultNewName = "26.3NEW";
    private const string DefaultSourceName = "26.2NEW";
    private const int MaxConcurrentDownloads = 8;
    private const int MaxDownloadAttempts = 3;
    private const string ShoulderSurfingJar = "ShoulderSurfing-Fabric-26.3-5.3.1-patched2.jar";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object ConsoleLock = new();

    private sealed record ModEntry(string Filename, string Sha1, long Size, string Url);

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pinkweiner-Upgrader/1.0");
        return client;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? prismDirArg = GetArg(args, "--prism-dir");
        string? manifestArg = GetArg(args, "--manifest");

        Console.WriteLine("========================================");
        Console.WriteLine("  Pinkweiner 26.3 Upgrader");
        Console.WriteLine("========================================");
        Console.WriteLine("Upgrades a Prism Launcher instance from Minecraft 26.2 to 26.3,");
        Console.WriteLine("keeping your configs, resource packs, and settings.");
        Console.WriteLine("Your old instance is never touched -- a new one is created.");
        Console.WriteLine();

        // 1. Find Prism's data dir.
        string? prismDir = FindPrismDir(prismDirArg);
        if (prismDir is null)
        {
            return 0; // user quit at the path prompt
        }
        string instancesDir = Path.Combine(prismDir, "instances");

        // 2. Pick the source instance (or fresh install).
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

        // 3. New instance name (handles collisions).
        (string newName, NewInstanceAction action) = ChooseNewName(instancesDir);
        string newInstanceDir = Path.Combine(instancesDir, newName);
        bool resume = action == NewInstanceAction.Resume;

        // 4. Warn if Prism or Minecraft is running.
        if (!RunningCheck())
        {
            return 0; // user quit
        }

        // 5. Ask about singleplayer worlds (upgrade only, and only if they exist).
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

        // 6. Create the new instance (deleting first if the user chose that).
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

        // 7. Copy the user's files from the old instance (upgrade only).
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

        // 8. Extract the built-in jars (custom mods + patched jars, not on Modrinth).
        var embedded = GetEmbeddedMods();
        Console.WriteLine($"Extracting {embedded.Count} built-in mod(s)...");
        foreach (var (fileName, bytes) in embedded)
        {
            File.WriteAllBytes(Path.Combine(newModsDir, fileName), bytes);
            Console.WriteLine("  + " + fileName);
        }

        // 9. Download the manifest mods.
        double totalMb = manifest.Sum(m => m.Size) / 1_000_000.0;
        Console.WriteLine($"Downloading {manifest.Count} mods ({totalMb:F1} MB)...");
        var (downloaded, skipped, failed) = await DownloadModsAsync(manifest, newModsDir);
        Console.WriteLine();

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

    // ----- manifest -----

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
            string filename = el.GetProperty("filename").GetString()
                ?? throw new InvalidOperationException("Manifest entry is missing a filename.");
            string sha1 = el.GetProperty("sha1").GetString()
                ?? throw new InvalidOperationException($"Manifest entry '{filename}' is missing a sha1.");
            long size = el.GetProperty("size").GetInt64();
            string url = el.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"Manifest entry '{filename}' is missing a url.");
            mods.Add(new ModEntry(filename, sha1, size, url));
        }
        if (mods.Count == 0)
        {
            throw new InvalidOperationException("The mod manifest is empty.");
        }
        return mods;
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

                // Resume support: skip files that already exist with a matching hash.
                if (File.Exists(dest)
                    && new FileInfo(dest).Length == mod.Size
                    && Sha1Hex(dest).Equals(mod.Sha1, StringComparison.OrdinalIgnoreCase))
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
            if (new FileInfo(tmp).Length != mod.Size)
            {
                File.Delete(tmp);
                return false;
            }
            if (!Sha1Hex(tmp).Equals(mod.Sha1, StringComparison.OrdinalIgnoreCase))
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

    private static string Sha1Hex(string path)
    {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
    }
}
