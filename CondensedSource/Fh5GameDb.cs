using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>FH5 PC GameDB codec, reconstructed and verified offline. Uses only
/// saved data tables; authenticates the header and every encrypted chunk tag.
/// No process access, captured native code execution, or installed-file writes.</summary>
public static class Fh5GameDb
{
    const int Header = 36, Chunk = 0x20000, Slot = Chunk + 16;
    static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fh5-data.json", true));
    static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fh5-mac.json", false));
    static readonly ParallelOptions Workers = new() { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
    static readonly uint[] Crc = BuildCrc();

    public static bool HasFraming(long length) => length >= Header + Slot && (length - Header) % Slot == 0;

    // FH5 and FH6 both have 36-byte framing. A valid FH5 header MAC, not the
    // filename or length alone, selects this profile before any payload decode.
    public static bool HasHeaderAuthentication(long length, ReadOnlySpan<byte> header)
    {
        if (!HasFraming(length) || header.Length < Header) return false;
        long payload = (length - Header) / Slot * Chunk;
        if (payload > int.MaxValue) return false;
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (padding >= Chunk || padding >= payload) return false;
        Span<byte> expected = stackalloc byte[16];
        Write(expected, HeaderMac((int)payload, header));
        return CryptographicOperations.FixedTimeEquals(expected, header.Slice(20, 16));
    }

    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] padded = DecryptPadded(encrypted);
        return padded.AsSpan(0, SqliteLength(padded)).ToArray();
    }

    public static byte[] Encrypt(byte[] sqlite, byte[] template)
    {
        if (SqliteLength(sqlite) != sqlite.Length)
            throw new InvalidDataException("FH5 SQLite length does not match its page count. Checkpoint the DB before exporting.");
        byte[] old = DecryptPadded(template); // Authenticate the complete template, not only its header.
        int length = checked((sqlite.Length + Chunk - 1) / Chunk * Chunk);
        byte[] padded = new byte[length];
        old.AsSpan(0, Math.Min(old.Length, length)).CopyTo(padded);
        sqlite.CopyTo(padded, 0); // Preserve original padding for byte-exact unchanged round trips.
        Scramble(padded);
        byte[] result = new byte[checked(Header + length / Chunk * Slot)];
        template.AsSpan(0, 16).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), checked((uint)(length - sqlite.Length)));
        Write(result.AsSpan(20), HeaderMac(length, result));
        var tags = new UInt128[length / Chunk];
        var data = Data.Value; var mac = Mac.Value;
        Parallel.For(0, tags.Length, Workers, n => tags[n] = mac.Cmac(padded.AsSpan(n * Chunk, Chunk)));
        UInt128 previous = Read(result);
        for (int n = 0; n < tags.Length; n++)
        {
            int output = Header + n * Slot;
            for (int i = 0; i < Chunk; i += 16)
            {
                previous = data.Inverse(Read(padded.AsSpan(n * Chunk + i)) ^ previous);
                Write(result.AsSpan(output + i), previous);
            }
            previous = data.Inverse(tags[n] ^ previous);
            Write(result.AsSpan(output + Chunk), previous);
        }
        // The caller also checks its saved snapshot; do not expose unchecked ciphertext.
        if (!Decrypt(result).AsSpan().SequenceEqual(sqlite))
            throw new InvalidDataException("Authenticated FH5 output did not reproduce the edited database.");
        return result;
    }

    static byte[] DecryptPadded(byte[] encrypted)
    {
        if (!HasHeaderAuthentication(encrypted.Length, encrypted))
            throw new InvalidDataException("FH5 GameDB header authentication failed. This build/key is unsupported or the file is damaged.");
        int count = (encrypted.Length - Header) / Slot, length = checked(count * Chunk);
        byte[] plain = new byte[length];
        var data = Data.Value; var mac = Mac.Value;
        try
        {
            Parallel.For(0, count, Workers, n =>
            {
                int input = Header + n * Slot;
                UInt128 previous = Read(encrypted.AsSpan(n == 0 ? 0 : input - 16));
                Span<byte> output = plain.AsSpan(n * Chunk, Chunk);
                for (int i = 0; i < Chunk; i += 16)
                {
                    UInt128 current = Read(encrypted.AsSpan(input + i));
                    Write(output[i..], data.Forward(current) ^ previous);
                    previous = current;
                }
                Span<byte> stored = stackalloc byte[16], actual = stackalloc byte[16];
                Write(stored, data.Forward(Read(encrypted.AsSpan(input + Chunk))) ^ previous);
                Write(actual, mac.Cmac(output));
                if (!CryptographicOperations.FixedTimeEquals(stored, actual))
                    throw new InvalidDataException($"FH5 GameDB authentication failed in chunk {n + 1}. No output was written.");
            });
        }
        catch (AggregateException error)
        {
            ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
        Scramble(plain);
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(16));
        if (SqliteLength(plain) != length - padding)
            throw new InvalidDataException("FH5 SQLite page count and authenticated container padding differ.");
        return plain;
    }

    static UInt128 HeaderMac(int length, ReadOnlySpan<byte> header)
    {
        Span<byte> value = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)length));
        header[..20].CopyTo(value[4..]);
        return Mac.Value.Cmac(value);
    }

    static int SqliteLength(ReadOnlySpan<byte> value)
    {
        if (value.Length < 100 || !value[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("FH5 payload is not SQLite.");
        int page = BinaryPrimitives.ReadUInt16BigEndian(value[16..]); if (page == 1) page = 65536;
        uint pages = BinaryPrimitives.ReadUInt32BigEndian(value[28..]);
        long size = (long)page * pages;
        if (page < 512 || (page & (page - 1)) != 0 || pages == 0 || size > value.Length)
            throw new InvalidDataException("Invalid FH5 SQLite page size/count.");
        return checked((int)size);
    }

    static void Scramble(Span<byte> value)
    {
        const uint seed = 0x03F33F6E; uint step = seed;
        for (int offset = 0; offset < value.Length; offset += 4, step = unchecked(step + seed + 1))
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                byte b = (byte)(step >> (i * 8)); if (b is >= 65 and <= 90) b += 32;
                crc = Crc[(crc ^ b) & 255] ^ (crc >> 8);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(value[offset..], BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]) ^ ~crc);
        }
    }

    static uint[] BuildCrc()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint v = i; for (int j = 0; j < 8; j++) v = (v >> 1) ^ ((v & 1) != 0 ? 0xEDB88320u : 0);
            table[i] = v;
        }
        return table;
    }
    static UInt128 Read(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt128LittleEndian(value);
    static void Write(Span<byte> output, UInt128 value) => BinaryPrimitives.WriteUInt128LittleEndian(output, value);
}
