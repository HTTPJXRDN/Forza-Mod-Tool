using System.IO;
using System.IO.Compression;
using FH6CarEditor;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Stages the localization companion for a full DB export. Never writes installed game files.</summary>
internal sealed class TrackSpacingLabelExport : IDisposable
{
    internal static string Folder(string database) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, "mediapc", "stripped", "stringtables");
    readonly string output;
    readonly string parent;
    string? staging;
    bool complete;
    sealed record Pending(string Relative, byte[] Bytes, byte[]? Original);
    readonly List<Pending> files = new(), published = new();
    readonly List<string> createdDirectories = new();

    TrackSpacingLabelExport(string database)
    {
        output = Folder(database);
        parent = Path.GetDirectoryName(Path.GetFullPath(database))!;
    }

    internal static TrackSpacingLabelExport Prepare(string sqlite, string database, string? template = null, string? stringTablesFolder = null)
    {
        var package = new TrackSpacingLabelExport(database);
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            // FM8/FH2 do not use these generated track-width display records.
            var game = GameDbIdentity.Detect(db);
            if (game is not (ForzaGame.Horizon5 or ForzaGame.Horizon6)) return package;
            var strings = TrackSpacingLabels.RequiredStrings(db);
            if (strings.Count == 0) return package;
            var baselines = Baselines(game, sqlite, template, stringTablesFolder);
            foreach (var (language, baseline) in baselines)
            {
                var table = new UpgradeStringTable(baseline.Bytes);
                if (table.Version != (game == ForzaGame.Horizon6 ? 0x800 : 0x400))
                    throw new InvalidDataException("Upgrades.str belongs to a different game: " + baseline.Source);
                string relative = Path.Combine(language, "Upgrades.str"), target = Path.Combine(package.output, relative);
                byte[]? original = File.Exists(target) ? ReadTable(target) : null;
                byte[] merged = original == null ? baseline.Bytes : table.Merge(original);
                byte[] changed = new UpgradeStringTable(merged).Add(strings);
                // Installed tables are shared across cars. Export only locales
                // that need an update. A correct earlier export is reused too.
                bool unchanged = original == null ? changed.AsSpan().SequenceEqual(baseline.Bytes) :
                    new UpgradeStringTable(original).SameContents(new UpgradeStringTable(changed));
                if (!unchanged)
                    package.files.Add(new(relative, changed, original));
            }
            if (package.files.Count == 0) return package;
            var installedRoots = game == ForzaGame.Horizon6 ? ThumbnailInstallDiscovery.GameRoots(false) : ThumbnailInstallDiscovery.Horizon5GameRoots();
            if (installedRoots.Any(root => Path.GetFullPath(Path.Combine(root, "mediapc", "stripped", "stringtables")).Equals(package.output, StringComparison.OrdinalIgnoreCase)) ||
                (stringTablesFolder != null && Path.GetFullPath(stringTablesFolder).TrimEnd(Path.DirectorySeparatorChar).Equals(package.output, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose an export folder outside the installed game. Install the generated stringtables folder manually.");
            package.staging = Path.Combine(package.parent, ".track-width-" + Guid.NewGuid().ToString("N") + ".partial");
            Directory.CreateDirectory(package.staging);
            foreach (var file in package.files)
            {
                string path = Path.Combine(package.staging, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Bytes);
            }
            return package;
        }
        catch { package.Dispose(); throw; }
    }

    internal void Publish()
    {
        if (staging == null) return;
        foreach (var file in files)
        {
            string target = Path.Combine(output, file.Relative);
            if (file.Original == null ? File.Exists(target) : !File.Exists(target) || !ReadTable(target).AsSpan().SequenceEqual(file.Original))
                throw new IOException("Exported string tables changed during export. Try again.");
        }
        foreach (var file in files)
        {
            string target = Path.Combine(output, file.Relative);
            EnsureDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(staging, file.Relative), target, true);
            published.Add(file);
        }
    }
    internal void Complete() { complete = true; }
    public void Dispose()
    {
        if (!complete)
        {
            foreach (var file in published.AsEnumerable().Reverse())
            {
                string target = Path.Combine(output, file.Relative);
                try
                {
                    if (!File.Exists(target) || !ReadTable(target).AsSpan().SequenceEqual(file.Bytes)) continue;
                    if (file.Original == null) File.Delete(target);
                    else
                    {
                        string restore = Path.Combine(staging!, file.Relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(restore)!);
                        File.WriteAllBytes(restore, file.Original); File.Move(restore, target, true);
                    }
                }
                catch { }
            }
            // Never recursively remove the shared mediapc folder. Remove only
            // empty directories that this export created, deepest first.
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                try { Directory.Delete(directory, false); } catch { }
        }
        if (staging != null && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent &&
            Path.GetFileName(staging).StartsWith(".track-width-", StringComparison.Ordinal) && staging.EndsWith(".partial", StringComparison.Ordinal))
            try { Directory.Delete(staging, true); } catch { }
    }

    void EnsureDirectory(string directory)
    {
        var missing = new Stack<string>();
        for (string? path = directory; path != null && !Directory.Exists(path); path = Path.GetDirectoryName(path)) missing.Push(path);
        while (missing.TryPop(out string? path)) { Directory.CreateDirectory(path); createdDirectories.Add(path); }
    }
    static byte[] ReadTable(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Upgrades.str is too large: " + path);
        return File.ReadAllBytes(path);
    }

    sealed record Baseline(byte[] Bytes, string Source);
    static Dictionary<string, Baseline> Baselines(ForzaGame game, string sqlite, string? template, string? stringTablesFolder = null)
    {
        string gameCode = game == ForzaGame.Horizon6 ? "FH6" : "FH5";
        var baselines = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);
        // Small native fallbacks permit portable/offline exports. Installed
        // native tables and then installed overrides replace these baselines.
        var assembly = typeof(TrackSpacingLabelExport).Assembly;
        string prefix = "FH6LocalCryptoTool.TrackLabels." + gameCode + ".";
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".str.gz", StringComparison.Ordinal)))
        {
            string language = resource[prefix.Length..^7];
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress); using var bytes = new MemoryStream(); gzip.CopyTo(bytes);
            baselines.Add(language, new(bytes.ToArray(), "bundled clean " + gameCode + " " + language));
        }
        if (baselines.Count == 0) throw new InvalidDataException("The bundled track-width string tables are missing.");

        if (stringTablesFolder != null)
        {
            stringTablesFolder = Path.GetFullPath(stringTablesFolder);
            ValidateFolder(stringTablesFolder);
            if (OverlayFolder(stringTablesFolder, game, baselines, new(StringComparer.OrdinalIgnoreCase)) == 0)
                throw new InvalidDataException("No Upgrades.str table was found in the selected stringtables folder: " + stringTablesFolder);
            return baselines;
        }

        var roots = new List<string>();
        foreach (string? file in new[] { template, sqlite })
        {
            var dir = file == null ? null : Directory.GetParent(Path.GetFullPath(file));
            for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "media", "stripped", "StringTables")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "MediaPC", "stripped", "StringTables")))
                {
                    // A nearby export folder can belong to another supported
                    // game. Do not mistake it for this DB's installed strings.
                    string overrides = Path.Combine(dir.FullName, "MediaPC", "stripped", "StringTables");
                    string? sample = Directory.Exists(overrides) ? Directory.EnumerateDirectories(overrides)
                        .Select(d => Path.Combine(d, "Upgrades.str")).FirstOrDefault(File.Exists) : null;
                    if (sample != null && !Directory.Exists(Path.Combine(dir.FullName, "media", "stripped", "StringTables")) &&
                        new UpgradeStringTable(ReadInstalledTable(sample, null, archive: false)!.Bytes).Version != (game == ForzaGame.Horizon6 ? 0x800 : 0x400)) continue;
                    roots.Add(dir.FullName);
                }
        }
        roots.AddRange(game == ForzaGame.Horizon6 ? ThumbnailInstallDiscovery.GameRoots(false) : ThumbnailInstallDiscovery.Horizon5GameRoots());
        string? root = roots.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(UsableRoot);
        if (root == null) return baselines;
        // Resolve the same precedence as the game before opening files. A
        // loose override makes the corresponding lower-priority ZIP unused.
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string media in new[] { "MediaPC", "media" })
        {
            string folder = Path.Combine(root, media, "stripped", "StringTables");
            if (!Directory.Exists(folder)) continue;
            OverlayFolder(folder, game, baselines, resolved);
        }
        return baselines;
    }

    internal static void ValidateFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("String-table folder not found: " + folder);
        if (!Directory.EnumerateFiles(folder, "*.zip").Any(p => ValidLanguage(Path.GetFileNameWithoutExtension(p))) &&
            !Directory.EnumerateDirectories(folder).Any(d => ValidLanguage(Path.GetFileName(d)) && File.Exists(Path.Combine(d, "Upgrades.str"))))
            throw new InvalidDataException("Choose the stringtables folder containing language ZIPs or language folders, such as EN\\Upgrades.str: " + folder);
    }

    static int OverlayFolder(string folder, ForzaGame game, Dictionary<string, Baseline> baselines, HashSet<string> resolved)
    {
        int added = 0;
        foreach (string directory in Directory.EnumerateDirectories(folder))
        {
            string lang = Path.GetFileName(directory), file = Path.Combine(directory, "Upgrades.str");
            if (!ValidLanguage(lang) || resolved.Contains(lang) || !File.Exists(file)) continue;
            baselines[lang] = ReadInstalledTable(file, game, archive: false)!;
            resolved.Add(lang); added++;
        }
        foreach (string zipPath in Directory.EnumerateFiles(folder, "*.zip"))
        {
            string lang = Path.GetFileNameWithoutExtension(zipPath);
            if (!ValidLanguage(lang) || resolved.Contains(lang)) continue;
            if (ReadInstalledTable(zipPath, game, archive: true) is not Baseline table) continue;
            baselines[lang] = table;
            resolved.Add(lang); added++;
        }
        return added;
    }

    static Baseline? ReadInstalledTable(string path, ForzaGame? game, bool archive)
    {
        try
        {
            byte[] bytes;
            string source = path;
            if (archive)
            {
                using var zip = ZipFile.OpenRead(path);
                var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').Split('/').Last().Equals("Upgrades.str", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length == 0) return null;
                if (entries.Length != 1 || entries[0].Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Expected one Upgrades.str entry, no larger than 16 MB.");
                using var stream = entries[0].Open(); using var data = new MemoryStream(); stream.CopyTo(data);
                bytes = data.ToArray();
                source += " / " + entries[0].FullName;
            }
            else bytes = ReadTable(path);
            var table = new UpgradeStringTable(bytes);
            if (game != null && table.Version != (game == ForzaGame.Horizon6 ? 0x800 : 0x400))
                throw new InvalidDataException("Upgrades.str belongs to a different game.");
            return new(bytes, source);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Cannot read string table" + (archive ? " ZIP" : "") + " \"" + path + "\": " + ex.Message, ex);
        }
    }

    static bool UsableRoot(string root)
    {
        // Xbox placeholders can look installed while their files cannot be
        // read. Do not let them hide a usable Steam install or offline fallback.
        // An existing override is always reviewed, so corrupt custom strings
        // fail explicitly rather than being silently replaced with stock data.
        string overrides = Path.Combine(root, "MediaPC", "stripped", "StringTables");
        if (Directory.Exists(overrides) && (Directory.EnumerateDirectories(overrides).Any(d => File.Exists(Path.Combine(d, "Upgrades.str"))) ||
            Directory.EnumerateFiles(overrides, "*.zip").Any())) return true;
        string native = Path.Combine(root, "media", "stripped", "StringTables");
        try
        {
            if (Directory.Exists(native) && Directory.EnumerateDirectories(native).Any(d =>
                ValidLanguage(Path.GetFileName(d)) && File.Exists(Path.Combine(d, "Upgrades.str")))) return true;
            string[] archives = Directory.Exists(native) ? Directory.GetFiles(native, "*.zip").Where(p => ValidLanguage(Path.GetFileNameWithoutExtension(p))).ToArray() : [];
            if (archives.Length == 0)
            {
                string loose = Path.Combine(native, "EN", "Upgrades.str");
                if (!File.Exists(loose)) return false;
                _ = new UpgradeStringTable(File.ReadAllBytes(loose)); return true;
            }
            foreach (string archive in archives)
            {
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/').Split('/').Last().Equals("Upgrades.str", StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length > 16 * 1024 * 1024) return false;
                using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                _ = new UpgradeStringTable(bytes.ToArray());
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
    static bool ValidLanguage(string name) => name.Length is >= 2 and <= 8 && name.All(c => char.IsAsciiLetter(c) || c is '-' or '_');
}
