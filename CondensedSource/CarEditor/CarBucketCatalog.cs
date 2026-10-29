using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FH6CarEditor;

internal sealed record CarBucketChoice(long Id, string Name, bool CanAssign)
{
    public string DisplayName => CanAssign ? Name : Name + " (group)";
    public string Details => $"Bucket ID {Id}" + (CanAssign ? "" : " · parent category, not assigned directly to cars");
    public override string ToString() => DisplayName;
}

internal static class CarBucketCatalog
{
    // English labels verified against the reference CarBuckets.str. Key by the
    // actual string reference, never by bucket ID: a custom/re-numbered table
    // must not inherit a guessed category name. Installed text takes priority.
    static readonly IReadOnlyDictionary<ulong, string> ReferenceLabels = new Dictionary<ulong, string>
    {
        [12810209389255790099UL] = "Cult Cars", [12810209389255790483UL] = "GT Cars",
        [12810209389255790355UL] = "Hot Hatch", [12810209389255789715UL] = "Iconic Rally",
        [12810209389255789587UL] = "Muscle", [12810209389255789971UL] = "Offroad",
        [12810209389255789843UL] = "Saloon Cars", [12810209389255789203UL] = "Sports Cars",
        [12810209389255789075UL] = "Supercars", [12810209389433000376UL] = "World Classics",
        [12810209389433000248UL] = "Modern Supercars", [12810209389433000120UL] = "Retro Supercars",
        [12810209389432999992UL] = "Hypercars", [12810209389433000888UL] = "Retro Super Saloons",
        [12810209389433000632UL] = "Utility Heroes", [12810209389433000504UL] = "Retro Sports Cars",
        [12810209389433001400UL] = "Modern Sports Cars", [12810209389433001272UL] = "Modern Super Saloons",
        [12810209389433049528UL] = "Classic Racers", [12810209389433049400UL] = "Cult Cars",
        [12810209389433049272UL] = "Rare Classics", [12810209389433049144UL] = "Hot Hatch",
        [12810209389433050040UL] = "Retro Hot Hatch", [12810209389433049912UL] = "Super Hot Hatch",
        [12810209389433049784UL] = "Extreme Track Toys", [12810209389433050552UL] = "Classic Muscle",
        [12810209389433050424UL] = "Rods and Customs", [12810209389433033144UL] = "Retro Muscle",
        [12810209389433033016UL] = "Modern Muscle", [12810209389433032888UL] = "Retro Rally",
        [12810209389433032760UL] = "Classic Rally", [12810209389433033656UL] = "Rally Monsters",
        [12810209389433033528UL] = "Modern Rally", [12810209389433033400UL] = "GT Cars",
        [12810209389433033272UL] = "Super GT", [12810209389433034168UL] = "Unlimited Offroad",
        [12810209389433034040UL] = "Sports Utility Heroes", [12810209389432951224UL] = "Offroad",
        [12810209389432951096UL] = "Unlimited Buggies", [12810209389432950968UL] = "Classic Sports Cars",
        [12810209389432950840UL] = "Track Toys", [12810209389432951736UL] = "Vintage Racers",
        [12810209389432951608UL] = "Trucks", [12810209389432951480UL] = "Buggies",
        [12810209389432951352UL] = "Drift Cars", [12810209389432952248UL] = "Pickups & 4x4's",
        [12810209389432952120UL] = "UTV's", [12810209389432934840UL] = "Eclectic Domestics",
        [12810209389432934712UL] = "Retro Racers",
    };

    internal static IReadOnlyList<CarBucketChoice> Read(IEnumerable<(long Id, string Name, long? Parent)> definitions, string? sourcePath)
        => ReadForGame(definitions, sourcePath, false);

    internal static IReadOnlyList<CarBucketChoice> ReadForGame(IEnumerable<(long Id, string Name, long? Parent)> definitions, string? sourcePath, bool fh2)
    {
        var rows = definitions.ToArray();
        if (rows.Length > 4096 || rows.Select(r => r.Id).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Unsupported or ambiguous car bucket definitions.");
        var parents = rows.Where(r => r.Parent.HasValue).Select(r => r.Parent!.Value).ToHashSet();
        var labels = InstalledLabels(sourcePath, fh2);
        return rows.Select(r => new CarBucketChoice(r.Id, Resolve(r.Id, r.Name, labels), !parents.Contains(r.Id)))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id).ToArray();
    }

    static string Resolve(long id, string name, IReadOnlyDictionary<uint, string> labels)
    {
        if (name.StartsWith("_&", StringComparison.Ordinal) && ulong.TryParse(name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out ulong key))
            return labels.GetValueOrDefault(unchecked((uint)key)) ?? ReferenceLabels.GetValueOrDefault(key) ?? $"Bucket {id} (untranslated)";
        return string.IsNullOrWhiteSpace(name) ? $"Bucket {id} (unnamed)" : name.Trim();
    }

    static IReadOnlyDictionary<uint, string> InstalledLabels(string? sourcePath, bool fh2)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var directory = Directory.GetParent(Path.GetFullPath(sourcePath));
            for (int i = 0; directory != null && i < 4; i++, directory = directory.Parent)
            {
                paths.Add(Path.Combine(directory.FullName, "StringTables", "EN", "CarBuckets.str"));
                paths.Add(Path.Combine(directory.FullName, "media", "stripped", "StringTables", "EN", "CarBuckets.str"));
            }
        }
        // Adjacent install first; portable copies still have the verified labels
        // above, and may discover newer matching references in an installed game.
        foreach (string path in paths.Concat((fh2 ? Enumerable.Empty<string>() : ThumbnailInstallDiscovery.GameRoots(false))
            .Select(root => Path.Combine(root, "media", "stripped", "StringTables", "EN", "CarBuckets.str"))))
        {
            try { if (File.Exists(path)) return ReadStringTable(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or System.Security.SecurityException) { }
        }
        return new Dictionary<uint, string>();
    }

    internal static Dictionary<uint, string> ReadStringTable(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 136 or > 4_194_304) throw new InvalidDataException("Unsupported bucket string table size.");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (bytes[0] != 0 || bytes[1] != 8) throw new InvalidDataException("Unsupported bucket string table format.");
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        int tables = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(130));
        int final = checked(132 + tables * 4);
        if (tables is < 1 or > 16 || final > bytes.Length) throw new InvalidDataException("Invalid bucket string table header.");
        var values = new Dictionary<uint, string>();
        for (int i = 0; i < tables; i++)
        {
            int at = checked((int)U32(132 + i * 4));
            if (at != final || at > bytes.Length - 12) throw new InvalidDataException("Invalid bucket string table offset.");
            int tableSize = checked((int)U32(at)), stringSize = checked((int)U32(at + 4)), count = checked((int)U32(at + 8));
            int start = checked(at + 12 + count * 8), end = checked(at + 12 + tableSize);
            if (count is < 0 or > 100000 || start > end || end > bytes.Length || checked(start + stringSize) != end)
                throw new InvalidDataException("Invalid bucket string table bounds.");
            for (int j = 0; j < count; j++)
            {
                uint key = U32(at + 12 + j * 8);
                int text = checked(start + (int)U32(at + 16 + j * 8));
                if (text < start || text >= end) throw new InvalidDataException("Invalid bucket label offset.");
                int zero = Array.IndexOf(bytes, (byte)0, text, end - text);
                if (zero < 0) throw new InvalidDataException("Unterminated bucket label.");
                string value = new UTF8Encoding(false, true).GetString(bytes, text, zero - text);
                if (i == 0 && !values.TryAdd(key, value) && values[key] != value) throw new InvalidDataException("Ambiguous bucket label.");
            }
            final = end;
        }
        if (final != bytes.Length) throw new InvalidDataException("Trailing bucket string data.");
        return values;
    }
}
