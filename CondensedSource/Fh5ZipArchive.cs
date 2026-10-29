using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FH6LocalCryptoTool;

/// <summary>Streaming ZIP32 workspaces for authenticated FH5 method-22 entries
/// and ordinary stored/DEFLATE ZIPs. Preserves original metadata and untouched
/// payloads; large cinematics never require an archive-sized byte array.</summary>
public static class Fh5ZipArchive
{
    public const string ManifestName = "fh5-zip-roundtrip.json";
    const string TemplateName = ".__original.zip", Format = "FH5-standard-ZIP-workspace-v1";
    const int MaxEntries = 65534, MaxDirectory = 32 * 1024 * 1024, MaxEncrypted = 128 * 1024 * 1024;
    const long MaxArchive = uint.MaxValue - 1L, MaxEntry = 2L * 1024 * 1024 * 1024, MaxTotal = 16L * 1024 * 1024 * 1024;
    static readonly uint[] CrcTable = BuildCrc();
    sealed record Manifest(string Format, string OriginalName, string TemplateSha256);
    sealed record Entry(string Name, long Offset, byte[] Header, byte[] Central, long PackedSize, long Size, uint Crc, ushort Method)
    {
        public long DataOffset => Offset + Header.Length;
        public bool Directory => Name.EndsWith('/');
        public bool AlignBefore { get; set; }
    }
    sealed record Archive(List<Entry> Entries, byte[] End, bool AlignCentral);
    sealed record Content(long Size, uint Crc, byte[] Hash);
    public sealed record Progress(int Completed, int EntryCount, long OutputBytes, string EntryName);
    public sealed record ArchiveInfo(int EntryCount, int EncryptedEntries, long PlaintextBytes, string OriginalName);
    public sealed record ExtractResult(int EntryCount, int EncryptedEntries, long OutputBytes, string OutputDirectory);
    public sealed record RepackResult(int EntryCount, int ChangedEntries, string OriginalName, string OutputPath);

    public static bool IsWorkspace(string path) => Directory.Exists(path)
        ? File.Exists(Path.Combine(path, ManifestName))
        : Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    // Ordinary ZIPs carry no game identity. They use the same preservation
    // workflow without claiming FH5 encryption. Every encrypted header must
    // authenticate before this reader is selected instead of the FH6 reader.
    public static bool CanHandle(string path)
    {
        try
        {
            using var file = Open(path); var archive = Parse(file);
            foreach (var entry in archive.Entries.Where(e => e.Method == 22 && !e.Directory))
                if (!Fh5AssetContainer.HasHeaderAuthentication(entry.PackedSize, ReadAt(file, entry.DataOffset, 36))) return false;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    public static ArchiveInfo Validate(string path, IProgress<Progress>? progress = null)
    {
        using var file = Open(path); var archive = Parse(file); long total = 0; int completed = 0;
        foreach (var entry in archive.Entries)
        {
            total += Decode(file, entry, Stream.Null).Size;
            progress?.Report(new(++completed, archive.Entries.Count, total, entry.Name));
        }
        return new(archive.Entries.Count, archive.Entries.Count(e => e.Method == 22 && !e.Directory), total, Path.GetFileName(path));
    }

    public static ExtractResult Extract(string source, string outputDirectory, IProgress<Progress>? progress = null)
    {
        using var file = Open(source); var archive = Parse(file);
        string target = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Extraction needs a new directory; previous output is never overwritten.");
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool owned = false;
        try
        {
            Directory.CreateDirectory(staging); owned = true; long total = 0; int completed = 0;
            foreach (var entry in archive.Entries)
            {
                string destination = MotorsportLzxArchive.Destination(staging, entry.Name);
                if (entry.Directory) Directory.CreateDirectory(destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                    total += Decode(file, entry, output).Size;
                }
                progress?.Report(new(++completed, archive.Entries.Count, total, entry.Name));
            }
            file.Position = 0;
            using (var template = new FileStream(Path.Combine(staging, TemplateName), FileMode.CreateNew, FileAccess.Write)) file.CopyTo(template);
            file.Position = 0;
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, Path.GetFileName(source), Convert.ToHexString(SHA256.HashData(file))), new JsonSerializerOptions { WriteIndented = true });
            using (var output = new FileStream(Path.Combine(staging, ManifestName), FileMode.CreateNew, FileAccess.Write)) output.Write(manifest);
            Directory.Move(staging, target); owned = false;
            return new(archive.Entries.Count, archive.Entries.Count(e => e.Method == 22 && !e.Directory), total, target);
        }
        finally { if (owned) DeleteOwnedStaging(staging, target); }
    }

    public static string OriginalName(string workspace) => ReadManifest(WorkspaceRoot(workspace)).OriginalName;

    public static RepackResult Repack(string workspace, string outputPath, IProgress<Progress>? progress = null)
    {
        string root = WorkspaceRoot(workspace); var manifest = ReadManifest(root);
        string destination = Path.GetFullPath(outputPath);
        if (destination.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export outside the ZIP workspace so its entry set stays unchanged.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("ZIP output already exists; choose a new filename.");
        using var source = Open(Path.Combine(root, TemplateName));
        if (!Convert.ToHexString(SHA256.HashData(source)).Equals(manifest.TemplateSha256, StringComparison.Ordinal))
            throw new InvalidDataException("ZIP workspace template fingerprint changed.");
        var archive = Parse(source); CheckWorkspace(root, archive);
        var plans = new Dictionary<Entry, (Content Content, bool Changed)>(); int changed = 0, completed = 0; long total = 0;
        foreach (var entry in archive.Entries)
        {
            var original = Decode(source, entry, Stream.Null);
            Content edited = original;
            if (!entry.Directory)
            {
                using var input = Open(MotorsportLzxArchive.Destination(root, entry.Name), MaxEntry);
                edited = Transfer(input, Stream.Null, input.Length);
            }
            bool different = edited.Size != original.Size || !edited.Hash.AsSpan().SequenceEqual(original.Hash);
            if (entry.Method == 22 && edited.Size > MaxEncrypted) throw new InvalidDataException("Edited encrypted entry exceeds the 128 MiB plaintext limit: " + entry.Name);
            if (different) changed++;
            total = checked(total + edited.Size); if (total > MaxTotal) throw new InvalidDataException("Edited ZIP exceeds the 16 GiB plaintext limit.");
            plans.Add(entry, (edited, different));
            progress?.Report(new(++completed, archive.Entries.Count * 2, total, entry.Name));
        }
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial"; bool owned = false;
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                owned = true;
                if (changed == 0) { source.Position = 0; source.CopyTo(output); }
                else
                {
                    var centrals = new Dictionary<Entry, byte[]>();
                    foreach (var entry in archive.Entries.OrderBy(e => e.Offset))
                    {
                        if (entry.AlignBefore) WritePadding(output);
                        long offset = output.Position; byte[] header = AlignHeader(entry, offset), central = (byte[])entry.Central.Clone();
                        output.Write(header); long dataOffset = output.Position;
                        var plan = plans[entry];
                        if (!plan.Changed) CopyRange(source, entry.DataOffset, entry.PackedSize, output);
                        else
                        {
                            using var input = Open(MotorsportLzxArchive.Destination(root, entry.Name), MaxEntry);
                            Content written;
                            if (entry.Method == 22)
                            {
                                using var compressed = new MemoryStream();
                                using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) written = Transfer(input, deflate, plan.Content.Size);
                                if (compressed.Length > MaxEncrypted) throw new InvalidDataException("Encrypted entry exceeds the 128 MiB packed limit.");
                                output.Write(Fh5AssetContainer.Encrypt(compressed.ToArray(), ReadAt(source, entry.DataOffset, checked((int)entry.PackedSize))));
                            }
                            else if (entry.Method == 8)
                            {
                                using var deflate = new DeflateStream(output, CompressionLevel.Optimal, true); written = Transfer(input, deflate, plan.Content.Size);
                            }
                            else written = Transfer(input, output, plan.Content.Size);
                            if (!written.Hash.AsSpan().SequenceEqual(plan.Content.Hash)) throw new InvalidDataException("Workspace entry changed during rebuilding: " + entry.Name);
                        }
                        long end = output.Position, packed = end - dataOffset;
                        Put(header, 14, plan.Content.Crc); Put(header, 18, checked((uint)packed)); Put(header, 22, checked((uint)plan.Content.Size));
                        Put(central, 16, plan.Content.Crc); Put(central, 20, checked((uint)packed)); Put(central, 24, checked((uint)plan.Content.Size)); Put(central, 42, checked((uint)offset));
                        PatchExtras(header, 30 + U16(header, 26), U16(header, 28), checked((uint)dataOffset), local: true);
                        PatchExtras(central, 46 + U16(central, 28), U16(central, 30), checked((uint)dataOffset));
                        output.Position = offset; output.Write(header); output.Position = end; centrals.Add(entry, central);
                        if (output.Length > MaxArchive) throw new InvalidDataException("Rebuilt archive exceeds ZIP32 limits.");
                    }
                    if (archive.AlignCentral) WritePadding(output);
                    long directoryOffset = output.Position;
                    foreach (var entry in archive.Entries) output.Write(centrals[entry]);
                    byte[] endRecord = (byte[])archive.End.Clone(); Put(endRecord, 12, checked((uint)(output.Position - directoryOffset))); Put(endRecord, 16, checked((uint)directoryOffset)); output.Write(endRecord);
                }
                output.Flush(true);
                var verify = Parse(output);
                if (verify.Entries.Count != archive.Entries.Count) throw new InvalidDataException("Rebuilt ZIP entry count differs.");
                var originals = archive.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
                foreach (var entry in verify.Entries)
                {
                    var original = originals[entry.Name]; var expected = plans[original].Content;
                    if (!Decode(output, entry, Stream.Null).Hash.AsSpan().SequenceEqual(expected.Hash)) throw new InvalidDataException("Rebuilt ZIP verification failed: " + entry.Name);
                    progress?.Report(new(++completed, archive.Entries.Count * 2, total, entry.Name));
                }
                // Do not silently export an older snapshot if an editor changed
                // an initially unchanged file while other entries were rebuilt.
                foreach (var entry in archive.Entries.Where(e => !e.Directory))
                {
                    using var input = Open(MotorsportLzxArchive.Destination(root, entry.Name), MaxEntry);
                    var expected = plans[entry].Content;
                    if (input.Length != expected.Size || !SHA256.HashData(input).AsSpan().SequenceEqual(expected.Hash))
                        throw new InvalidDataException("Workspace changed during ZIP export: " + entry.Name);
                }
            }
            File.Move(partial, destination); owned = false;
            return new(archive.Entries.Count, changed, manifest.OriginalName, destination);
        }
        finally { if (owned && File.Exists(partial)) File.Delete(partial); }
    }

    static Archive Parse(Stream file)
    {
        if (file.Length is < 22 or > MaxArchive) throw new InvalidDataException("Archive exceeds supported ZIP32 bounds.");
        int tailSize = (int)Math.Min(file.Length, 65557); byte[] tail = ReadAt(file, file.Length - tailSize, tailSize); int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--) if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10)) throw new InvalidDataException("Only complete single-disk ZIP32 archives are supported.");
        int count = U16(tail, end + 10); long offset = U32(tail, end + 16), size = U32(tail, end + 12);
        if (count is 0 or > MaxEntries || size > MaxDirectory || offset + size != file.Length - tailSize + end) throw new InvalidDataException("Invalid/unsupported ZIP32 directory.");
        byte[] directory = ReadAt(file, offset, checked((int)size)); int pos = 0; long total = 0;
        var entries = new List<Entry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        for (int n = 0; n < count; n++)
        {
            Bounds(directory, pos, 46); if (U32(directory, pos) != 0x02014b50) throw new InvalidDataException("Invalid ZIP central entry.");
            int nameLength = U16(directory, pos + 28), extras = U16(directory, pos + 30), comment = U16(directory, pos + 32), recordSize = 46 + nameLength + extras + comment;
            Bounds(directory, pos, recordSize); byte[] central = directory.AsSpan(pos, recordSize).ToArray();
            ushort flags = U16(central, 8), method = U16(central, 10);
            if ((flags & ~0x0806) != 0 || (flags & 6) != 0 && method != 8 || method is not (0 or 8 or 22) || U16(central, 34) != 0 || (U32(central, 38) >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsupported ZIP flags, symlink, descriptor, disk or compression method.");
            string name = ((flags & 0x800) != 0 ? new UTF8Encoding(false, true) : Encoding.GetEncoding(437)).GetString(central, 46, nameLength);
            MotorsportLzxArchive.SafeName(name);
            if (!names.Add(name.TrimEnd('/')) || new[] { ManifestName, TemplateName, Fh6ZipArchive.ManifestName, MotorsportLzxArchive.ManifestName, MotorsportTrackArchive.ReportName }.Contains(name.Split('/')[0], StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP has colliding/reserved workspace paths.");
            if (!name.EndsWith('/')) files.Add(name);
            long packed = U32(central, 20), plain = U32(central, 24), localOffset = U32(central, 42); uint crc = U32(central, 16);
            if (plain > MaxEntry || method == 22 && !name.EndsWith('/') && (packed > MaxEncrypted || plain > MaxEncrypted) || localOffset >= offset) throw new InvalidDataException("ZIP entry exceeds supported bounds.");
            byte[] local = ReadAt(file, localOffset, 30); ushort localMethod = U16(local, 8);
            bool emptyDirectory = name.EndsWith('/') && packed == 0 && plain == 0 && crc == 0;
            if (U32(local, 0) != 0x04034b50 || U16(local, 6) != flags || localMethod != method && !(emptyDirectory && method == 22 && localMethod == 0)
                || U32(local, 14) != crc || U32(local, 18) != packed || U32(local, 22) != plain || U16(local, 26) != nameLength)
                throw new InvalidDataException("ZIP local/central metadata disagree: " + name);
            int headerSize = 30 + nameLength + U16(local, 28);
            if (localOffset + headerSize + packed > offset) throw new InvalidDataException("ZIP entry overlaps its directory.");
            byte[] header = ReadAt(file, localOffset, headerSize);
            if (!header.AsSpan(30, nameLength).SequenceEqual(central.AsSpan(46, nameLength))) throw new InvalidDataException("ZIP local/central names differ.");
            if (name.EndsWith('/') && (!emptyDirectory || localMethod is not (0 or 22))) throw new InvalidDataException("Invalid empty ZIP directory.");
            if (method == 0 && packed != plain) throw new InvalidDataException("Stored ZIP entry has inconsistent lengths.");
            PatchExtras(header, 30 + nameLength, U16(header, 28), checked((uint)(localOffset + headerSize)), verify: true, local: true);
            PatchExtras(central, 46 + nameLength, extras, checked((uint)(localOffset + headerSize)), verify: true);
            total = checked(total + plain); if (total > MaxTotal) throw new InvalidDataException("ZIP exceeds the 16 GiB plaintext limit.");
            entries.Add(new(name, localOffset, header, central, packed, plain, crc, method)); pos += recordSize;
        }
        if (pos != directory.Length) throw new InvalidDataException("ZIP central directory count/size mismatch.");
        long next = 0;
        foreach (var entry in entries.OrderBy(e => e.Offset))
        {
            if (entry.Offset < next) throw new InvalidDataException("ZIP local entries overlap.");
            if (entry.Offset != next) { CheckPadding(file, next, entry.Offset); entry.AlignBefore = true; }
            next = entry.DataOffset + entry.PackedSize;
            string[] parts = entry.Name.TrimEnd('/').Split('/');
            for (int i = 1; i < parts.Length; i++) if (files.Contains(string.Join('/', parts.Take(i)))) throw new InvalidDataException("ZIP file/directory paths collide.");
        }
        bool alignCentral = next != offset; if (alignCentral) CheckPadding(file, next, offset);
        return new(entries, tail.AsSpan(end).ToArray(), alignCentral);
    }

    static Content Decode(Stream source, Entry entry, Stream output)
    {
        if (entry.Directory) return new(0, 0, SHA256.HashData(Array.Empty<byte>()));
        if (entry.Method == 22)
        {
            byte[] packed = ReadAt(source, entry.DataOffset, checked((int)entry.PackedSize));
            using var compressed = new MemoryStream(Fh5AssetContainer.Decrypt(packed));
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress); var content = Transfer(deflate, output, entry.Size);
            if (content.Crc != entry.Crc) throw new InvalidDataException("ZIP CRC mismatch: " + entry.Name);
            return content;
        }
        source.Position = entry.DataOffset; using var segment = new Segment(source, entry.PackedSize);
        using Stream plain = entry.Method == 8 ? new DeflateStream(segment, CompressionMode.Decompress, true) : segment;
        var result = Transfer(plain, output, entry.Size);
        if (result.Crc != entry.Crc) throw new InvalidDataException("ZIP CRC mismatch: " + entry.Name);
        return result;
    }

    static Content Transfer(Stream source, Stream output, long expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); uint crc = uint.MaxValue; long size = 0;
        byte[] buffer = new byte[128 * 1024]; int read;
        while ((read = source.Read(buffer)) != 0)
        {
            size = checked(size + read); if (size > expected || size > MaxEntry) throw new InvalidDataException("ZIP/workspace content exceeds its declared size.");
            hash.AppendData(buffer, 0, read); foreach (byte value in buffer.AsSpan(0, read)) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            output.Write(buffer, 0, read);
        }
        if (size != expected) throw new InvalidDataException("ZIP/workspace plaintext length mismatch.");
        return new(size, ~crc, hash.GetHashAndReset());
    }

    static Manifest ReadManifest(string root)
    {
        using var input = Open(Path.Combine(root, ManifestName), 64 * 1024);
        var manifest = JsonSerializer.Deserialize<Manifest>(input) ?? throw new InvalidDataException("Missing ZIP workspace metadata.");
        if (manifest.Format != Format || string.IsNullOrWhiteSpace(manifest.OriginalName) || Path.GetFileName(manifest.OriginalName) != manifest.OriginalName || !manifest.OriginalName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid ZIP workspace metadata.");
        MotorsportLzxArchive.SafeName(manifest.OriginalName); return manifest;
    }
    static string WorkspaceRoot(string path)
    {
        string root = Path.GetFullPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); RejectReparse(root); return root;
    }
    static void CheckWorkspace(string root, Archive archive)
    {
        var expected = archive.Entries.Where(e => !e.Directory).Select(e => MotorsportLzxArchive.Destination(root, e.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Add(Path.Combine(root, ManifestName)); expected.Add(Path.Combine(root, TemplateName)); var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(); stack.Push(root); int count = 0;
        while (stack.TryPop(out var directory))
        {
            if (++count > MaxEntries * 4) throw new InvalidDataException("Too many workspace directories.");
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparse(child);
                if (Directory.Exists(child)) stack.Push(child);
                else if (!expected.Contains(child) || !found.Add(child)) throw new InvalidDataException("Unexpected workspace file: " + Path.GetRelativePath(root, child));
            }
        }
        if (!expected.SetEquals(found)) throw new InvalidDataException("A ZIP workspace file is missing. Adding/removing entries is not supported.");
    }
    static byte[] AlignHeader(Entry entry, long offset)
    {
        int start = 30 + U16(entry.Header, 26); using var extras = new MemoryStream(); bool aligned = false;
        for (int at = start; at < entry.Header.Length;)
        {
            int size = U16(entry.Header, at + 2);
            if (U16(entry.Header, at) == 0x1123 && !entry.Header.AsSpan(at + 4, size).ContainsAnyExcept((byte)0) && entry.DataOffset % 4096 == 0)
            { if (aligned) throw new InvalidDataException("Multiple local alignment fields."); aligned = true; }
            else extras.Write(entry.Header.AsSpan(at, 4 + size)); at += 4 + size;
        }
        if (!aligned) return (byte[])entry.Header.Clone();
        int padding = (int)((4096 - (offset + start + extras.Length + 4) % 4096) % 4096); byte[] field = new byte[4 + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x1123); BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), (ushort)padding); extras.Write(field);
        if (extras.Length > ushort.MaxValue) throw new InvalidDataException("ZIP local alignment overflow.");
        byte[] result = new byte[start + (int)extras.Length]; entry.Header.AsSpan(0, start).CopyTo(result); extras.ToArray().CopyTo(result, start); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), (ushort)extras.Length); return result;
    }
    static void PatchExtras(byte[] record, int start, int length, uint offset, bool verify = false, bool local = false)
    {
        int end = start + length;
        for (int at = start; at < end;)
        {
            if (at + 4 > end) throw new InvalidDataException("Truncated ZIP extra field."); int size = U16(record, at + 2); ushort id = U16(record, at);
            if (at + 4 + size > end || id == 1) throw new InvalidDataException("ZIP64 or malformed extra field is unsupported.");
            if (id == 0x1123 && !(local && !record.AsSpan(at + 4, size).ContainsAnyExcept((byte)0)))
            {
                if (size != 4 || verify && U32(record, at + 4) != offset) throw new InvalidDataException("Invalid Forza ZIP data-offset field.");
                if (!verify) Put(record, at + 4, offset);
            }
            at += 4 + size;
        }
    }
    static void CheckPadding(Stream source, long start, long end)
    {
        long length = end - start;
        if (length is < 6 or > 4101 || end % 4096 != 0) throw new InvalidDataException("Unknown ZIP gap/padding.");
        byte[] bytes = ReadAt(source, start, (int)length);
        if (U32(bytes, 0) != uint.MaxValue || U16(bytes, 4) != length - 6 || bytes.AsSpan(6).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Invalid ZIP padding record.");
    }
    static void WritePadding(Stream output)
    {
        int size = (int)((4096 - output.Position % 4096) % 4096); if (size == 0) return; if (size < 6) size += 4096;
        byte[] bytes = new byte[size]; Put(bytes, 0, uint.MaxValue); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)(size - 6)); output.Write(bytes);
    }
    static FileStream Open(string path, long limit = MaxArchive)
    {
        RejectReparse(path); var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) { file.Dispose(); throw new InvalidDataException("File exceeds supported size: " + Path.GetFileName(path)); } return file;
    }
    static void RejectReparse(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ZIP workspace links/junctions are not supported."); }
    static byte[] ReadAt(Stream file, long offset, int size)
    {
        if (offset < 0 || size < 0 || offset > file.Length - size) throw new InvalidDataException("ZIP bounds exceed the file."); byte[] result = new byte[size]; file.Position = offset; file.ReadExactly(result); return result;
    }
    static void CopyRange(Stream source, long offset, long count, Stream output)
    {
        source.Position = offset; using var segment = new Segment(source, count); segment.CopyTo(output); if (segment.Remaining != 0) throw new InvalidDataException("Truncated ZIP payload.");
    }
    static void DeleteOwnedStaging(string staging, string target)
    {
        // Only our exact sibling staging path can be removed on failure.
        string prefix = Path.GetFullPath(target) + ".";
        if (!Path.GetFullPath(staging).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !staging.EndsWith(".partial", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe staging cleanup path.");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }
    static void Bounds(byte[] bytes, int at, int size) { if (at < 0 || size < 0 || at > bytes.Length - size) throw new InvalidDataException("Truncated ZIP metadata."); }
    static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    static void Put(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at, 4), value);
    static uint[] BuildCrc()
    {
        var result = new uint[256]; for (uint i = 0; i < 256; i++) { uint value = i; for (int n = 0; n < 8; n++) value = value >> 1 ^ ((value & 1) == 0 ? 0 : 0xEDB88320u); result[i] = value; } return result;
    }
    sealed class Segment : Stream
    {
        readonly Stream source; readonly long length;
        public Segment(Stream source, long length) { this.source = source; this.length = length; Remaining = length; }
        public long Remaining { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => length - Remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { int n = source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]); Remaining -= n; return n; }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
