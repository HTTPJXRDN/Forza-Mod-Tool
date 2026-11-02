using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace FH6LocalCryptoTool.GarageViewer;

/// <summary>Optional car labels from the same installation as the selected GameDB.</summary>
internal sealed class GarageCatalogLabels(string source)
{
    readonly Dictionary<string, IReadOnlyDictionary<uint, string>> _tables = new(StringComparer.Ordinal);

    internal string? Resolve(string reference, string table)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (!reference.StartsWith("_&", StringComparison.Ordinal)) return reference.Trim();
        if (!ulong.TryParse(reference.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out ulong key) ||
            (uint)(key >> 32) != UpgradeStringTable.Hash(table)) return null;
        if (!_tables.TryGetValue(table, out var values)) _tables[table] = values = Read(table);
        return values.TryGetValue((uint)key, out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    IReadOnlyDictionary<uint, string> Read(string table)
    {
        foreach (string folder in Folders(source))
        {
            string loose = Path.Combine(folder, "EN", table + ".str");
            string archive = Path.Combine(folder, "EN.zip");
            try
            {
                byte[]? bytes = null;
                if (File.Exists(loose))
                {
                    using var stream = File.OpenRead(loose);
                    if (stream.Length is < 140 or > 4_194_304) return new Dictionary<uint, string>();
                    bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
                }
                else if (File.Exists(archive))
                {
                    if (new FileInfo(archive).Length > 64_000_000) return new Dictionary<uint, string>();
                    using var zip = ZipFile.OpenRead(archive);
                    if (zip.Entries.Count > 4096) return new Dictionary<uint, string>();
                    var matches = zip.Entries.Where(e => e.FullName.Replace('\\', '/').Split('/').Last().Equals(table + ".str", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length > 1) return new Dictionary<uint, string>();
                    if (matches.Length == 0) continue;
                    var entry = matches[0]; if (entry.Length is < 140 or > 4_194_304) return new Dictionary<uint, string>();
                    using var stream = entry.Open(); bytes = new byte[checked((int)entry.Length)]; stream.ReadExactly(bytes);
                }
                if (bytes != null) return new UpgradeStringTable(bytes, table).Values;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
            {
                // Names are optional. An unreadable override must not substitute
                // labels from another install or block garage editing.
                return new Dictionary<uint, string>();
            }
        }
        return new Dictionary<uint, string>();
    }

    static IEnumerable<string> Folders(string path)
    {
        var parent = Directory.GetParent(Path.GetFullPath(path));
        if (parent == null) yield break;
        if (parent.Name.Equals("stripped", StringComparison.OrdinalIgnoreCase) && parent.Parent is { } media &&
            (media.Name.Equals("media", StringComparison.OrdinalIgnoreCase) || media.Name.Equals("mediapc", StringComparison.OrdinalIgnoreCase)) && media.Parent is { } root)
        {
            yield return Path.Combine(root.FullName, "MediaPC", "stripped", "stringtables");
            yield return Path.Combine(root.FullName, "media", "stripped", "stringtables");
        }
        else yield return Path.Combine(parent.FullName, "stringtables");
    }
}
