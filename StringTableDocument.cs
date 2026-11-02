using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>Editable native string tables with a self-contained, validated rebuild template.</summary>
public static class StringTableDocument
{
    const string Format = "FMT.StringTable.1";
    const int MaxBytes = 40_000_000;
    public static bool IsTable(string path) => Path.GetExtension(path).Equals(".str", StringComparison.OrdinalIgnoreCase);
    public static bool IsDocument(string path) => path.EndsWith(".str.json", StringComparison.OrdinalIgnoreCase);

    static UpgradeStringTable Read(byte[] bytes)
    {
        if (bytes.Length is < 140 or > 20_000_000) throw new InvalidDataException("Unsupported string-table size.");
        int end = Array.IndexOf(bytes, (byte)0, 2, 126);
        if (end < 3 || bytes.AsSpan(2, end - 2).ToArray().Any(c => c < 32 || c > 126))
            throw new InvalidDataException("The file is not a supported native string table.");
        return new UpgradeStringTable(bytes, Encoding.ASCII.GetString(bytes, 2, end - 2));
    }

    public static byte[] Decode(byte[] native)
    {
        var table = Read(native);
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Format, Table = table.TableName, Version = table.Version,
            Entries = table.Entries, NativeTemplate = Convert.ToBase64String(native)
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (result.Length > MaxBytes) throw new InvalidDataException("String-table JSON exceeds the size limit.");
        return result;
    }

    public static byte[] Rebuild(byte[] json)
    {
        if (json.Length is < 2 or > MaxBytes) throw new InvalidDataException("Unsupported string-table JSON size.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
            !root.TryGetProperty("Format", out var format) || format.GetString() != Format ||
            !root.TryGetProperty("NativeTemplate", out var template) || template.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("Entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Choose a .str.json file created by Decrypt. Edit only the Entries section.");
        byte[] original = template.GetBytesFromBase64(); var table = Read(original);
        if (!root.TryGetProperty("Table", out var name) || name.GetString() != table.TableName ||
            !root.TryGetProperty("Version", out var version) || !version.TryGetUInt16(out ushort nativeVersion) || nativeVersion != table.Version)
            throw new InvalidDataException("The string-table name or version disagrees with its template.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || !values.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new InvalidDataException("Entries must have unique keys and text values.");
        }
        byte[] result = table.Rewrite(values);
        if (result.Length > 20_000_000) throw new InvalidDataException("Rebuilt string table exceeds the size limit.");
        return result;
    }
}
