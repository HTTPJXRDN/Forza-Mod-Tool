using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FH6LocalCryptoTool;

namespace FH6CarEditor;

public sealed record CarThumbnailResult(BitmapSource? Image, string Status, string? Source = null);

/// <summary>Read-only stock/model preview lookup. Never uses a player's CTN garage cache.</summary>
public sealed class CarThumbnailLibrary
{
    sealed record Entry(string File, string? ZipEntry, int Rank);
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CarThumbnailResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly object _sync = new();
    const int MaxImageBytes = 8 * 1024 * 1024;
    static readonly Regex OfflineName = new(@"^(\d+)_[0-9a-f]{16}bm(\d+)_Big$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex IdName = new(@"^(?:thumbnail_)?(\d+)(?:_(big|small))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ResourceArchiveName = new(@"^RC\d+(HiRes)?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".swatchbin", ".webp", ".png", ".jpg", ".jpeg", ".bmp" };
    public string SourcePath { get; }
    public int Count => _entries.Count;

    public CarThumbnailLibrary(string path)
    {
        SourcePath = NormalizeSource(Path.GetFullPath(path));
        if (Directory.Exists(SourcePath)) IndexFolder(SourcePath, 0, new int[1]);
        else if (File.Exists(SourcePath) && Path.GetExtension(SourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) IndexArchive(SourcePath);
        else throw new FileNotFoundException("Choose a thumbnail folder or ZIP archive.", SourcePath);
        if (Count == 0) throw new InvalidDataException("No supported model thumbnails found. Choose the game's media/stripped folder (RC*.zip), OfflineThumbnails.zip or images named by Car ID / MediaName.");
    }

    static string NormalizeSource(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFiles(path).Any(p => Extensions.Contains(Path.GetExtension(p)) &&
            (IdName.IsMatch(Path.GetFileNameWithoutExtension(p)) || OfflineName.IsMatch(Path.GetFileNameWithoutExtension(p)) ||
             Regex.IsMatch(Path.GetFileNameWithoutExtension(p), @"^[A-Za-z0-9]+_[A-Za-z0-9_]+_\d{2}(?:_big)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))) return path;
        string? native = Fh2ThumbnailDiscovery.NativeFolders(path).FirstOrDefault();
        if (native != null) return native;
        // A package may be wrapped in an arbitrarily named outer directory.
        // Inspect one bounded child level, not the game's complete asset tree.
        foreach (string child in Directory.EnumerateDirectories(path).Take(128))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            native = Fh2ThumbnailDiscovery.NativeFolders(child).FirstOrDefault();
            if (native != null) return native;
        }
        return path;
    }

    void IndexFolder(string folder, int depth, int[] count)
    {
        // Deliberately bounded: a manually selected game root must not crawl the whole install/drive.
        if (depth > 5) return;
        foreach (string file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (++count[0] > 20000) throw new InvalidDataException("Too many files. Select the thumbnail folder itself instead of a game/drive root.");
            if (Extensions.Contains(Path.GetExtension(file))) Add(file, null);
            else if (ResourceArchiveName.IsMatch(Path.GetFileName(file)) || Path.GetFileName(file).Equals("OfflineThumbnails.zip", StringComparison.OrdinalIgnoreCase)) IndexArchive(file);
        }
        foreach (string child in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            try { IndexFolder(child, depth + 1, count); }
            catch (UnauthorizedAccessException) { }
        }
    }

    void IndexArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("Too many entries in thumbnail archive.");
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            if (entry.Length is > 0 and <= MaxImageBytes && Extensions.Contains(Path.GetExtension(entry.Name))) Add(path, entry.FullName);
    }

    void Add(string path, string? zipEntry)
    {
        string stem = Path.GetFileNameWithoutExtension((zipEntry ?? path).Replace('\\', '/'));
        // Normalize numeric IDs (FH6 pads shorter ordinals to four digits).
        var offline = OfflineName.Match(stem);
        var id = IdName.Match(stem);
        // Resource ZIPs also contain logos/decals with model-like names. Only
        // actual ID-named swatch thumbnails belong in this model preview index.
        if (Path.GetExtension(zipEntry ?? path).Equals(".swatchbin",StringComparison.OrdinalIgnoreCase) && !id.Success) return;
        string key;
        int rank;
        if (offline.Success && long.TryParse(offline.Groups[1].Value, out long offlineId)) { key = offlineId.ToString(CultureInfo.InvariantCulture); rank = offline.Groups[2].Value == "0" ? 1 : 2; }
        else if (id.Success && long.TryParse(id.Groups[1].Value, out long ordinal)) { key = ordinal.ToString(CultureInfo.InvariantCulture); rank = id.Groups[2].Value.Equals("small", StringComparison.OrdinalIgnoreCase) ? 3 : 0; }
        else {
            // Explicit MediaName image files only, not CTN references or substring matches.
            if (!Regex.IsMatch(stem, @"^[A-Za-z0-9]+_[A-Za-z0-9_]+_\d{2}(?:_big)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return;
            key = stem.EndsWith("_big", StringComparison.OrdinalIgnoreCase) ? stem[..^4] : stem;
            rank = 0;
        }
        // The RC*HiRes packs contain larger versions of the same exact car IDs.
        if (zipEntry != null && Path.GetExtension(zipEntry).Equals(".swatchbin", StringComparison.OrdinalIgnoreCase))
            rank += Path.GetFileNameWithoutExtension(path).EndsWith("HiRes", StringComparison.OrdinalIgnoreCase) ? -2 : -1;
        if (!_entries.TryGetValue(key, out var old) || rank < old.Rank) _entries[key] = new(path, zipEntry, rank);
    }

    public CarThumbnailResult Load(long carId, string mediaName)
    {
        string key = carId.ToString(CultureInfo.InvariantCulture);
        lock (_sync)
        {
            // Prefer an explicit model-name image if supplied by a mod creator.
            if (!_entries.TryGetValue(mediaName, out var entry) && !_entries.TryGetValue(key, out entry))
                return new(null, "No default thumbnail for this car in the selected source.");
            key = entry.File + "|" + entry.ZipEntry;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            CarThumbnailResult result;
            try {
                byte[] bytes;
                if (entry.ZipEntry != null) {
                    using var archive = ZipFile.OpenRead(entry.File);
                    var item = archive.GetEntry(entry.ZipEntry) ?? throw new FileNotFoundException();
                    if (item.Length is <= 0 or > MaxImageBytes) throw new InvalidDataException();
                    using var input = item.Open();
                    bytes = new byte[checked((int)item.Length)];input.ReadExactly(bytes);
                } else {
                    using var input = File.OpenRead(entry.File);
                    if (input.Length is <= 0 or > MaxImageBytes) throw new InvalidDataException();
                    bytes = new byte[checked((int)input.Length)];input.ReadExactly(bytes);
                }
                BitmapSource frame;
                if (Path.GetExtension(entry.ZipEntry ?? entry.File).Equals(".swatchbin", StringComparison.OrdinalIgnoreCase)) frame = SwatchThumbnailDecoder.Decode(bytes);
                else {
                    using var stream = new MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    frame = decoder.Frames[0];
                }
                if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096 || (long)frame.PixelWidth * frame.PixelHeight > 8_000_000) throw new InvalidDataException();
                BitmapSource image = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                double scale = Math.Min(1, Math.Min(1600d / frame.PixelWidth, 900d / frame.PixelHeight));
                if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                int stride = checked(image.PixelWidth * 4);
                byte[] pixels = new byte[checked(stride * image.PixelHeight)];image.CopyPixels(pixels, stride, 0);
                var snapshot = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                snapshot.Freeze();
                result = new(snapshot, "Default model preview · not your installed modifications", entry.ZipEntry ?? entry.File);
            } catch (Exception ex) when (ex is IOException or SystemException) {
                result = new(null, "Thumbnail could not be read. Check the source / Windows image decoder.");
            }
            if (_cache.Count >= 12) _cache.Clear();
            _cache[key] = result;
            return result;
        }
    }

    public static IEnumerable<string> FindSources(string? databasePath, bool motorsport)
        => FindSources(databasePath, motorsport ? ForzaGame.Motorsport8 : ForzaGame.Horizon6);

    public static IEnumerable<string> FindSources(string? databasePath, ForzaGame game)
    {
        if (game == ForzaGame.Horizon2)
        {
            foreach (string path in Fh2ThumbnailDiscovery.Sources(databasePath)) yield return path;
            yield break;
        }
        bool motorsport = game == ForzaGame.Motorsport8;
        // FM8's native LZX archives are not supported by the stock-image reader.
        if (motorsport) yield break;
        // Only inspect known paths, never recursively search drives. DB location wins.
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(databasePath)) {
            var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
            for (int i = 0; parent != null && parent.Parent != null && i < 10; i++, parent = parent.Parent) roots.Add(parent.FullName);
        }
        roots.AddRange(game == ForzaGame.Horizon5 ? ThumbnailInstallDiscovery.Horizon5GameRoots() : ThumbnailInstallDiscovery.GameRoots(motorsport));
        // Prefer the selected game's full stock packs; keep its offline fallback.
        // Native FM8 ZIPs use LZX compression, which this read-only preview does not extract.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Search all installs for the full packs before offering a sparse offline ZIP.
        foreach (string root in roots) {
            foreach (string suffix in new[] { "", "Stripped", @"media\Stripped", @"Content\media\Stripped" }) {
                string folder = Path.Combine(root,suffix);
                if (seen.Add(folder) && Directory.Exists(folder) && File.Exists(Path.Combine(folder,"RC0.zip")) && MatchesGame(folder, game)) yield return folder;
            }
        }
        foreach (string root in roots) {
            foreach (string suffix in new[] { @"UI\Textures\OfflineThumbnails.zip", @"media\UI\Textures\OfflineThumbnails.zip", @"Content\media\UI\Textures\OfflineThumbnails.zip" }) {
                string path = Path.Combine(root, suffix);
                if (seen.Add(path) && File.Exists(path)) yield return path;
            }
        }
    }

    static bool MatchesGame(string folder, ForzaGame game)
    {
        string database = Path.Combine(folder, "gamedbRC.slt");
        if (!File.Exists(database)) return true;
        try
        {
            var format = GameDbContainerFormat.Detect(database);
            return format switch
            {
                GameDbContainerFormat.Kind.Fh5TransformIt36 => game == ForzaGame.Horizon5,
                GameDbContainerFormat.Kind.Fh6Aes36 => game == ForzaGame.Horizon6,
                GameDbContainerFormat.Kind.Sqlite => GameDbIdentity.Detect(database) == game,
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForzaModTool", "car-thumbnail-sources.json");
    static readonly object SettingsSync = new();
    static string GameKey(ForzaGame game) => game switch { ForzaGame.Horizon2 => "FH2", ForzaGame.Horizon5 => "FH5", ForzaGame.Horizon6 => "FH6", ForzaGame.Motorsport8 => "FM8", _ => throw new ArgumentException("Choose a supported thumbnail game.", nameof(game)) };
    static Dictionary<string, string> ReadSources()
    {
        try {
            if (File.Exists(SettingsFile) && new FileInfo(SettingsFile).Length <= 16384)
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) ?? new();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    public static string? SavedSource(bool motorsport) => SavedSource(motorsport ? ForzaGame.Motorsport8 : ForzaGame.Horizon6);
    public static string? SavedSource(ForzaGame game)
    {
        lock (SettingsSync) return ReadSources().GetValueOrDefault(GameKey(game));
    }
    public static void SaveSource(bool motorsport, string? source) => SaveSource(motorsport ? ForzaGame.Motorsport8 : ForzaGame.Horizon6, source);
    public static void SaveSource(ForzaGame game, string? source)
    {
        lock (SettingsSync)
        {
            var paths = ReadSources(); string key = GameKey(game);
            if (source == null) paths.Remove(key); else paths[key] = Path.GetFullPath(source);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(paths));
        }
    }
}
