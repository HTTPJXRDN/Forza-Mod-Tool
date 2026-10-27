using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Authenticated FH5 profile inspection. No profile encryption or save editing.</summary>
public static class Fh5ProfileCrypto
{
    public const int MaxFileBytes = 128 * 1024 * 1024;
    const int Header = 36, Chunk = 512, Slot = 528;
    static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fh5-profile-data.json", false));
    static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fh5-profile-mac.json", false));

    public static bool HasHeaderAuthentication(long length, ReadOnlySpan<byte> header)
    {
        if (length < Header + Slot || length > MaxFileBytes || (length - Header) % Slot != 0 || header.Length < Header) return false;
        int padded = checked((int)((length - Header) / Slot * Chunk));
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (padding >= Chunk || padding >= padded) return false;
        Span<byte> input = stackalloc byte[24], tag = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)padded);
        header[..20].CopyTo(input[4..]);
        BinaryPrimitives.WriteUInt128LittleEndian(tag, Mac.Value.Cmac(input));
        return CryptographicOperations.FixedTimeEquals(tag, header.Slice(20, 16));
    }

    public static bool IsEncryptedProfile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[Header];
            return file.Read(header) == Header && HasHeaderAuthentication(file.Length, header);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsDatabaseExport(string path)
    {
        if (Path.GetFileName(path).Contains(".fh5-profile", StringComparison.OrdinalIgnoreCase)) return true;
        // Keep a renamed FH5 export out of the FH6 re-encryption path too.
        // The shared Career_Garage/PurchasedParts pair alone is not game identity.
        try
        {
            using (var file = File.OpenRead(path))
            {
                Span<byte> header = stackalloc byte[16];
                if (file.Read(header) != 16 || !header.SequenceEqual("SQLite format 3\0"u8)) return false;
            }
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('Career_Garage','BadgeTitleProgress','BadgeTitleUnlocks','PerkState','PerkUnlock')";
            return Convert.ToInt64(command.ExecuteScalar()) == 5;
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsPlainPayload(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> marker = stackalloc byte[4];
            if (file.Length > MaxFileBytes || file.Read(marker) != 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(marker) != ProfileContainer.MarkerProfile) return false;
            file.Position = 0;
            byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            return IsProfileDatabase(ProfileContainer.Parse(bytes).Database);
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // A manually decrypted/renamed section stream must not bypass the disabled
    // FH5 viewer and enter the editable FH6 profile path.
    public static bool IsProfileDatabase(byte[] database)
    {
        if (database.Length < 16 || !database.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) return false;
        Directory.CreateDirectory(TempWorkspace.Root);
        string work = Path.Combine(TempWorkspace.Root, "profile-schema-check-" + Guid.NewGuid().ToString("N") + ".sqlite");
        bool created = false;
        try
        {
            using (var output = new FileStream(work, FileMode.CreateNew, FileAccess.Write)) { created = true; output.Write(database); }
            return IsDatabaseExport(work);
        }
        finally { if (created) File.Delete(work); }
    }

    public static byte[] ReadInput(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < Header + Slot || file.Length > MaxFileBytes) throw new InvalidDataException("FH5 save exceeds safe size limits or is truncated.");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }

    public static byte[] Decrypt(byte[] encrypted)
    {
        if (!HasHeaderAuthentication(encrypted.Length, encrypted))
            throw new InvalidDataException("Not an authenticated supported FH5 profile save. The file may be damaged or use another crypto profile/build.");
        int count = (encrypted.Length - Header) / Slot;
        byte[] raw = new byte[count * Chunk];
        var data = Data.Value; var mac = Mac.Value;
        UInt128 previous = BinaryPrimitives.ReadUInt128LittleEndian(encrypted);
        Span<byte> actual = stackalloc byte[16], expected = stackalloc byte[16];
        for (int n = 0; n < count; n++)
        {
            int start = Header + n * Slot;
            Span<byte> plain = raw.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                UInt128 current = BinaryPrimitives.ReadUInt128LittleEndian(encrypted.AsSpan(start + i));
                BinaryPrimitives.WriteUInt128LittleEndian(plain[i..], data.Forward(current) ^ previous);
                previous = current;
            }
            UInt128 tag = BinaryPrimitives.ReadUInt128LittleEndian(encrypted.AsSpan(start + Chunk));
            BinaryPrimitives.WriteUInt128LittleEndian(actual, data.Forward(tag) ^ previous);
            BinaryPrimitives.WriteUInt128LittleEndian(expected, mac.Cmac(plain));
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new InvalidDataException($"FH5 save authentication failed in chunk {n + 1}. No output was written.");
            previous = tag;
        }
        int padding = (int)BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(16));
        if (raw.AsSpan(raw.Length - padding).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Nonzero FH5 save padding.");
        return ParsePayload(raw.AsSpan(0, raw.Length - padding));
    }

    // FH5 stores the four sections directly, without FH6's size/zlib envelope.
    // Native saves can sign padding=0 while carrying up to 511 zero bytes after
    // the database; allow only that bounded, fully authenticated zero tail.
    static byte[] ParsePayload(ReadOnlySpan<byte> raw)
    {
        uint[] markers = { ProfileContainer.MarkerProfile, ProfileContainer.MarkerSaveState, ProfileContainer.MarkerBinary, ProfileContainer.MarkerDatabase };
        int offset = 0;
        foreach (uint marker in markers)
        {
            if (raw.Length - offset < 8 || BinaryPrimitives.ReadUInt32LittleEndian(raw[offset..]) != marker)
                throw new InvalidDataException("Unsupported FH5 profile section framing.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw[(offset + 4)..]);
            if (size == 0 || size > raw.Length - offset - 8) throw new InvalidDataException("Truncated FH5 profile section.");
            offset += 8 + (int)size;
        }
        if (raw.Length - offset >= Chunk || raw[offset..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Unexpected bytes after the FH5 profile database.");
        byte[] payload = raw[..offset].ToArray();
        _ = ProfileContainer.Parse(payload);
        return payload;
    }

    public static ProfileContainer Load(string path) => ProfileContainer.Parse(Decrypt(ReadInput(path)));

    public static void Decrypt(string input, string output) => ExportDecrypted(input, output, databaseOnly: false);

    public static void DecryptDatabase(string input, string output) => ExportDecrypted(input, output, databaseOnly: true);

    static void ExportDecrypted(string input, string output, bool databaseOnly)
    {
        byte[] original = ReadInput(input);
        byte[] payload = Decrypt(original);
        var profile = ProfileContainer.Parse(payload);
        ValidateDatabase(profile.Database);
        if (!ReadInput(input).AsSpan().SequenceEqual(original)) throw new IOException("FH5 save changed during processing. Retry with a stable copy; no output written.");
        bool created = false;
        try { using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); created = true; file.Write(databaseOnly ? profile.Database : payload); }
        catch { if (created) File.Delete(output); throw; }
    }

    public static void ValidateDatabase(byte[] database)
    {
        string work = Path.Combine(TempWorkspace.Root, "fh5-profile-check-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var file = new FileStream(work, FileMode.CreateNew, FileAccess.Write)) file.Write(database);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = work, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA quick_check";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read()) throw new InvalidDataException("FH5 profile database failed quick_check.");
        }
        finally { if (File.Exists(work)) File.Delete(work); }
    }
}
