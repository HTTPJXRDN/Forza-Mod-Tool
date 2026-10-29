#if PROFILE_UNLOCK_TESTING
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FH6LocalCryptoTool;

/// <summary>Read-only installed-game definitions, captured immutably for each loaded profile.</summary>
public static class ProfileGameData
{
    internal sealed record Snapshot(string Path, string Fingerprint, uint[] OnlineThresholds,
        CharacterClothingUnlockState.Clothing[] Clothing, CarHornUnlockState.Horn[] Horns, EmoteUnlockState.Emote[] Emotes,
        ForzaLinkPhraseUnlockState.Phrase[] Phrases, BadgeCatalog.Badge[] Badges,
        CollectionJournalCatalog.Catalog? Journal, JournalRewardUnlockCatalog.Reward[] Rewards, string[] Warnings)
    {
        internal PlaylistActivityCatalog.Series[] Playlists { get; init; } = Array.Empty<PlaylistActivityCatalog.Series>();
        internal IReadOnlySet<int> DlcCarOrdinals { get; init; } = CollectionJournalCarPolicy.ReferenceDlcCars;
        internal Dictionary<ulong, CollectionJournalCatalog.Challenge>? Challenges { get; } = Journal?.Challenges.ToDictionary(c => c.Id);
        internal Dictionary<ulong, CollectionJournalCatalog.Category>? Categories { get; } = Journal?.Categories.ToDictionary(c => c.Id);
        internal Dictionary<ulong, BadgeCatalog.Badge> BadgeDefinitions { get; } = Badges.ToDictionary(b => b.SaveId);
        internal Dictionary<(JournalRewardKind, ulong), JournalRewardUnlockCatalog.Reward> RewardDefinitions { get; } = Rewards.ToDictionary(r => (r.Kind, r.Item));
    }
    public sealed record Status(bool Loaded, string Source, string Message);
    static readonly ConditionalWeakTable<ProfileContainer, Snapshot> Bindings = new();
    static readonly AsyncLocal<Snapshot?> Ambient = new();
    static readonly object Gate = new();
    static Snapshot? cached;
    static (string Path, long Size, long Time, long DbSize, long DbTime) cacheKey;
    static string SettingsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaModTool", "ProfileGameDataPath.txt");
    internal static Snapshot? Current => Ambient.Value;
    internal static Snapshot? For(ProfileContainer? container) => container != null && Bindings.TryGetValue(container, out var data) ? data : null;
    internal static IDisposable Use(ProfileContainer? container)
    {
        var previous = Ambient.Value; Ambient.Value = For(container) ?? previous;
        return new Scope(() => Ambient.Value = previous);
    }
    sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    internal static void Copy(ProfileContainer original, ProfileContainer next) { if (For(original) is { } snapshot) { Bindings.Remove(next); Bindings.Add(next, snapshot); } }
    public static Status GetStatus(ProfileContainer? container) => For(container) is { } data
        ? new(true, data.Path, $"Installed game data: {data.Clothing.Length} clothes · {data.Horns.Length} horns · {data.Emotes.Length} emotes · {data.Phrases.Length} phrases · {data.Badges.Length} badges · {data.Journal?.Challenges.Length ?? 0} Journal definitions."
            + (data.Warnings.Length == 0 ? "" : " Some new definition types are read-only; compatibility details are in the log."))
        : new(false, "Built-in reference", "Built-in definitions. Select ObjectModelGame.zip to detect newer game content. Unknown save entries remain read-only.");

    public static Status AttachAutomatically(ProfileContainer container)
    {
        try
        {
            var paths = Candidates().Where(File.Exists).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 1) return Attach(container, paths[0]);
            if (paths.Length is > 1 and <= 16 && paths.All(p => new FileInfo(p).Length is >= 100 and <= 536_870_912))
            {
                var fingerprints = paths.Select(p => { using var input = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(input)); }).Distinct(StringComparer.Ordinal).ToArray();
                if (fingerprints.Length == 1)
                    return Attach(container, paths.OrderByDescending(p => Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p)!, "stripped", "StringTables", "EN"))).First());
            }
            return new(false, "Built-in reference", paths.Length > 1 ? "Several game installs found. Select the matching ObjectModelGame.zip." : GetStatus(container).Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return new(false, "Built-in reference", "Game install could not be detected. Select ObjectModelGame.zip manually."); }
    }
    public static IReadOnlyList<string> Warnings(ProfileContainer? container) => For(container)?.Warnings ?? Array.Empty<string>();
    public static Status Attach(ProfileContainer container, string path, bool remember = false)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            var info = new FileInfo(path);
            if (!info.Name.Equals("ObjectModelGame.zip", StringComparison.OrdinalIgnoreCase) || info.Length is < 100 or > 536_870_912) throw new InvalidDataException("Select the game's ObjectModelGame.zip.");
            Snapshot snapshot;
            lock (Gate)
            {
                var database = new FileInfo(CollectionJournalCarPolicy.GameDbPath(path));
                var key = (path, info.Length, info.LastWriteTimeUtc.Ticks,
                    database.Exists ? database.Length : 0, database.Exists ? database.LastWriteTimeUtc.Ticks : 0);
                if (cached != null && cacheKey == key) snapshot = cached;
                else { snapshot = RuntimeProfileCatalog.Read(path); cached = snapshot; cacheKey = key; }
            }
            Bindings.Remove(container); Bindings.Add(container, snapshot);
            if (remember) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, path); }
            return GetStatus(container);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Xml.XmlException or NotSupportedException or System.Security.SecurityException)
        { return new(false, "Preserved current definitions", "Game data not loaded: " + error.Message); }
    }
    static IEnumerable<string> Candidates()
    {
        if (File.Exists(SettingsPath))
        {
            string saved = "";
            try { if (new FileInfo(SettingsPath).Length <= 4096) saved = File.ReadAllText(SettingsPath).Trim(); } catch (IOException) { }
            if (File.Exists(saved)) { yield return saved; yield break; }
        }
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            libraries.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            yield return System.IO.Path.Combine(drive.RootDirectory.FullName, "XboxGames", "Forza Horizon 6", "Content", "media", "ObjectModelGame.zip");
        }
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            if ((Registry.GetValue(key, "SteamPath", null) ?? Registry.GetValue(key, "InstallPath", null)) is string steam)
            {
                libraries.Add(steam); string config = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(config) && new FileInfo(config).Length < 1_000_000)
                    foreach (Match match in Regex.Matches(File.ReadAllText(config), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
        foreach (string library in libraries) yield return System.IO.Path.Combine(library, "steamapps", "common", "ForzaHorizon6", "media", "ObjectModelGame.zip");
    }
}

internal sealed class ObjectModelSource : IDisposable
{
    readonly FileStream stream;
    readonly ZipArchive zip;
    readonly Dictionary<ulong, (string Type, string File)> manifest;
    readonly Dictionary<ulong, XElement> objects = new();
    long decoded;
    internal string Fingerprint { get; }
    internal ObjectModelSource(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Fingerprint = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
            zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > 100_000 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count) throw new InvalidDataException("Invalid object archive index.");
            var root = Xml("source/manifest.xml").Root!;
            manifest = Prop(root, "ItemData").Elements("map_element").ToDictionary(e => ulong.Parse((string)e.Element("key")!.Attribute("value")!, CultureInfo.InvariantCulture), e =>
            { var value = e.Element("value")!; return (Value(value, "TypeId"), "source/" + Value(value, "FileName").Replace('\\', '/')); });
            if (manifest.Count is < 1 or > 100_000) throw new InvalidDataException("Invalid object manifest size.");
        }
        catch { zip?.Dispose(); stream.Dispose(); throw; }
    }
    XDocument Xml(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains(':')) throw new InvalidDataException("Unsafe object path.");
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing game definition.");
        if (entry.Length is < 8 or > 20_000_000 || (decoded += entry.Length) > 268_435_456) throw new InvalidDataException("Game definitions exceed size limits.");
        using var input = entry.Open(); byte[] bytes = new byte[(int)entry.Length]; input.ReadExactly(bytes);
        var bx = Bxml.Parse(bytes); if (!bx.RoundTripsCleanly(bytes)) throw new InvalidDataException("Game definition BXML does not round-trip.");
        return XDocument.Parse(bx.ToXml());
    }
    internal Dictionary<ulong, XElement> Data(string type)
    {
        var result = new Dictionary<ulong, XElement>();
        foreach (var pair in manifest.Where(p => p.Value.Type == type))
        {
            if (!objects.TryGetValue(pair.Key, out var obj))
            {
                obj = Xml(pair.Value.File).Root!.Element("object") ?? throw new InvalidDataException("Game object root missing.");
                if ((string?)obj.Attribute("type") != type || (string?)obj.Attribute("id") != pair.Key.ToString(CultureInfo.InvariantCulture) + ".2877453236") throw new InvalidDataException("Game object identity mismatch.");
                objects.Add(pair.Key, obj);
            }
            result.Add(pair.Key, obj);
        }
        if (result.Count == 0) throw new InvalidDataException("Missing game definition type: " + type);
        return result;
    }
    internal XElement Single(string type) => Data(type).Single().Value;
    internal static XElement Prop(XElement obj, string id) => obj.Elements("property").Single(p => (string?)p.Attribute("id") == id);
    internal static string Value(XElement obj, string id) => (string?)obj.Elements("property").SingleOrDefault(p => (string?)p.Attribute("id") == id)?.Attribute("value") ?? "";
    internal static ulong Hash(string text) { ulong hash = 14695981039346656037; foreach (byte b in Encoding.UTF8.GetBytes(text.ToLowerInvariant())) hash = unchecked((hash ^ b) * 1099511628211); return hash; }
    public void Dispose() { zip.Dispose(); stream.Dispose(); }
}
#endif
