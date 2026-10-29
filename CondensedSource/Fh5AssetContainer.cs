using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>FH5 General/File assets: authenticated 36-byte header, 512-byte
/// data/16-byte encrypted-MAC slots and continuous CBC chaining. Unlike the
/// GameDB profile, these bytes are not CRC-scrambled. ZIP handling is separate.</summary>
public static class Fh5AssetContainer
{
    const int Header = 36, Chunk = 512, Slot = Chunk + 16;
    static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fh5-file-data.json", true));
    static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fh5-file-mac.json", false));

    public static bool HasFraming(long length) => length >= Header + Slot && (length - Header) % Slot == 0;

    // FH5 and FH6 assets have the same size/layout. Only a valid MAC selects
    // FH5; filenames, extensions and plausible plaintext are not key detection.
    public static bool HasHeaderAuthentication(long length, ReadOnlySpan<byte> header)
    {
        if (!HasFraming(length) || header.Length < Header) return false;
        long padded = (length - Header) / Slot * Chunk;
        if (padded > int.MaxValue) return false;
        uint padding = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (padding >= Chunk || padding >= padded) return false;
        Span<byte> expected = stackalloc byte[16];
        Write(expected, HeaderMac((int)padded, header));
        return CryptographicOperations.FixedTimeEquals(expected, header.Slice(20, 16));
    }

    public static byte[] Decrypt(byte[] encrypted)
    {
        if (!HasHeaderAuthentication(encrypted.Length, encrypted))
            throw new InvalidDataException("FH5 General/File asset header authentication failed. Unsupported key/build or damaged file.");
        int count = (encrypted.Length - Header) / Slot;
        byte[] padded = new byte[checked(count * Chunk)];
        var data = Data.Value; var mac = Mac.Value;
        UInt128 previous = Read(encrypted);
        Span<byte> actual = stackalloc byte[16], expected = stackalloc byte[16];
        for (int n = 0; n < count; n++)
        {
            int input = Header + n * Slot;
            Span<byte> plain = padded.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                UInt128 current = Read(encrypted.AsSpan(input + i));
                Write(plain[i..], data.Forward(current) ^ previous);
                previous = current;
            }
            UInt128 encryptedTag = Read(encrypted.AsSpan(input + Chunk));
            Write(actual, data.Forward(encryptedTag) ^ previous);
            Write(expected, mac.Cmac(plain));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new InvalidDataException($"FH5 General/File asset authentication failed in chunk {n + 1}. No output was written.");
            previous = encryptedTag;
        }
        int padding = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(16)));
        if (padded.AsSpan(padded.Length - padding).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("FH5 asset contains nonzero bytes in its declared padding.");
        return padded.AsSpan(0, padded.Length - padding).ToArray();
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] template)
    {
        _ = Decrypt(template); // Authenticate every chunk of the original, not just its header.
        if (plaintext.Length == 0) throw new InvalidDataException("FH5 assets must contain at least one byte.");
        int length = checked((plaintext.Length + Chunk - 1) / Chunk * Chunk);
        byte[] padded = new byte[length];
        plaintext.CopyTo(padded, 0);
        byte[] result = new byte[checked(Header + length / Chunk * Slot)];
        template.AsSpan(0, 16).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), checked((uint)(length - plaintext.Length)));
        Write(result.AsSpan(20), HeaderMac(length, result));
        var data = Data.Value; var mac = Mac.Value;
        UInt128 previous = Read(result);
        for (int n = 0; n < length / Chunk; n++)
        {
            int output = Header + n * Slot;
            ReadOnlySpan<byte> plain = padded.AsSpan(n * Chunk, Chunk);
            for (int i = 0; i < Chunk; i += 16)
            {
                previous = data.Inverse(Read(plain[i..]) ^ previous);
                Write(result.AsSpan(output + i), previous);
            }
            previous = data.Inverse(mac.Cmac(plain) ^ previous);
            Write(result.AsSpan(output + Chunk), previous);
        }
        if (!Decrypt(result).AsSpan().SequenceEqual(plaintext))
            throw new InvalidDataException("FH5 asset authenticated round-trip verification failed. No output was written.");
        return result;
    }

    static UInt128 HeaderMac(int length, ReadOnlySpan<byte> header)
    {
        Span<byte> input = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(input, checked((uint)length));
        header[..20].CopyTo(input[4..]);
        return Mac.Value.Cmac(input);
    }
    static UInt128 Read(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt128LittleEndian(value);
    static void Write(Span<byte> output, UInt128 value) => BinaryPrimitives.WriteUInt128LittleEndian(output, value);
}
