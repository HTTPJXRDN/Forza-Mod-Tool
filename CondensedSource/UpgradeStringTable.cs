using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>Native v4/v8 string tables. Existing strings and section conventions are retained.</summary>
internal sealed class UpgradeStringTable
{
    sealed record Block(List<(uint Key, int Offset)> Pairs, byte[] Strings, int SizeBias, Dictionary<uint, string> Values);
    readonly byte[] raw;
    readonly int headerSize;
    readonly Encoding encoding;
    readonly int terminator;
    readonly List<Block> blocks = new();
    internal IReadOnlyDictionary<uint, string> Values => blocks[0].Values;
    internal ushort Version => BinaryPrimitives.ReadUInt16LittleEndian(raw);

    internal static uint Hash(string text)
    {
        uint value = uint.MaxValue;
        foreach (char c in text) { value ^= c; value = (value << 7) | (value >> 25); }
        return value;
    }
    internal static string Reference(string key) => "_&" + (((ulong)Hash("Upgrades") << 32) | Hash(key)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal UpgradeStringTable(byte[] bytes)
    {
        raw = bytes;
        if (raw.Length < 140) throw new InvalidDataException("Truncated Upgrades.str.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        encoding = version switch { 0x800 => new UTF8Encoding(false, true), 0x400 => new UnicodeEncoding(false, false, true),
            _ => throw new InvalidDataException("Unsupported Upgrades.str version.") };
        terminator = version == 0x400 ? 2 : 1;
        int nameEnd = Array.IndexOf(raw, (byte)0, 2, 126);
        if (nameEnd < 0 || Encoding.ASCII.GetString(raw, 2, nameEnd - 2) != "Upgrades")
            throw new InvalidDataException("Expected the Upgrades string table.");
        int count = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(130));
        if (count is not (1 or 2)) throw new InvalidDataException("Invalid Upgrades.str section count.");
        headerSize = 132 + count * 4;
        int end = headerSize;
        for (int section = 0; section < count; section++)
        {
            int start = Read(132 + section * 4);
            if (start != end || start > raw.Length - 12) throw new InvalidDataException("Invalid string-table section offset.");
            int size = Read(start), stringBytes = Read(start + 4), entries = Read(start + 8);
            if (size < 0 || stringBytes < 0 || entries < 0 || entries > (raw.Length - start - 12) / 8)
                throw new InvalidDataException("Invalid string-table section size.");
            int dataStart = start + 12 + entries * 8;
            if (stringBytes > raw.Length - dataStart) throw new InvalidDataException("Truncated localized strings.");
            end = dataStart + stringBytes;
            int bias = end - start - size;
            if (bias is not (0 or 12)) throw new InvalidDataException("Invalid string-table block size.");
            byte[] strings = raw.AsSpan(dataStart, stringBytes).ToArray();
            var pairs = new List<(uint, int)>(); var values = new Dictionary<uint, string>();
            for (int i = 0; i < entries; i++)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(start + 12 + i * 8));
                int offset = Read(start + 16 + i * 8);
                if (offset < 0 || offset >= strings.Length || offset % terminator != 0 || values.ContainsKey(key))
                    throw new InvalidDataException("Invalid localization key or string offset.");
                int stop = offset;
                while (stop <= strings.Length - terminator &&
                    (strings[stop] != 0 || (terminator == 2 && strings[stop + 1] != 0))) stop += terminator;
                if (stop > strings.Length - terminator) throw new InvalidDataException("Unterminated localized string.");
                values.Add(key, encoding.GetString(strings, offset, stop - offset)); pairs.Add((key, offset));
            }
            blocks.Add(new(pairs, strings, bias, values));
        }
        if (end != raw.Length) throw new InvalidDataException("Trailing string-table data.");
    }
    int Read(int offset) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset));

    internal byte[] Add(IReadOnlyDictionary<string, string> additions)
    {
        var pending = new Dictionary<uint, (string Key, string Text)>();
        foreach (var (name, text) in additions)
        {
            if (text.Contains('\0') || name.Contains('\0')) throw new InvalidDataException("Invalid track-width label.");
            uint key = Hash(name);
            if (blocks[0].Values.TryGetValue(key, out string? existing))
            {
                if (existing != text || (blocks.Count > 1 && (!blocks[1].Values.TryGetValue(key, out string? keyName) || keyName != name)))
                    throw new InvalidDataException("Track-width localization conflicts with an existing string: " + name);
            }
            else if (pending.TryGetValue(key, out var other) && other != (name, text))
                throw new InvalidDataException("Track-width localization key collision.");
            else pending[key] = (name, text);
        }
        if (pending.Count == 0) return raw.ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(raw, 0, headerSize);
        for (int section = 0; section < blocks.Count; section++)
        {
            var block = blocks[section];
            var pairs = new List<(uint Key, int Offset)>(block.Pairs);
            using var strings = new MemoryStream(); strings.Write(block.Strings);
            foreach (var (key, item) in pending)
            {
                if (block.Values.ContainsKey(key)) throw new InvalidDataException("Localization sections disagree.");
                pairs.Add((key, checked((int)strings.Length)));
                strings.Write(encoding.GetBytes(section == 0 ? item.Text : item.Key));
                strings.Write(new byte[terminator]);
            }
            long start = output.Position;
            output.Position = 132 + section * 4; writer.Write(checked((int)start)); output.Position = start;
            writer.Write(checked(12 + pairs.Count * 8 + (int)strings.Length - block.SizeBias));
            writer.Write(checked((int)strings.Length)); writer.Write(pairs.Count);
            foreach (var pair in pairs) { writer.Write(pair.Key); writer.Write(pair.Offset); }
            strings.Position = 0; strings.CopyTo(output);
        }
        byte[] result = output.ToArray(); var verified = new UpgradeStringTable(result);
        for (int section = 0; section < blocks.Count; section++)
            foreach (var old in blocks[section].Values)
                if (!verified.blocks[section].Values.TryGetValue(old.Key, out string? text) || text != old.Value)
                    throw new InvalidDataException("An existing localized string changed.");
        foreach (var (key, item) in pending)
            if (verified.Values[key] != item.Text || (blocks.Count > 1 && verified.blocks[1].Values[key] != item.Key))
                throw new InvalidDataException("The generated track-width label did not resolve.");
        return result;
    }

    internal byte[] Merge(byte[] additional)
    {
        var other = new UpgradeStringTable(additional);
        if (Version != other.Version) throw new InvalidDataException("The export folder contains string tables from another game. Choose a separate export folder.");
        var additions = new Dictionary<string, string>();
        foreach (var entry in other.Values)
        {
            if (Values.TryGetValue(entry.Key, out string? text))
            {
                if (text != entry.Value || (blocks.Count > 1 && other.blocks.Count > 1 &&
                    blocks[1].Values.GetValueOrDefault(entry.Key) != other.blocks[1].Values.GetValueOrDefault(entry.Key)))
                    throw new InvalidDataException("Existing exported strings conflict with the installed strings. Choose a separate export folder.");
                continue;
            }
            if (other.blocks.Count < 2 || !other.blocks[1].Values.TryGetValue(entry.Key, out string? name) || Hash(name) != entry.Key)
                throw new InvalidDataException("Cannot preserve an unnamed exported localization key. Choose a separate export folder.");
            additions.Add(name, entry.Value);
        }
        return Add(additions);
    }

    internal bool SameContents(UpgradeStringTable other) => Version == other.Version && blocks.Count == other.blocks.Count &&
        blocks.Select((block, i) => block.Values.Count == other.blocks[i].Values.Count &&
            block.Values.All(p => other.blocks[i].Values.TryGetValue(p.Key, out string? text) && text == p.Value)).All(equal => equal);
}
