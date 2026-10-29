using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FH6LocalCryptoTool;

/// <summary>FH6 C_ProfileData blob: CryptoContainer -> size header -> zlib -> save payload.</summary>
public static class ProfileData
{
    public static byte[] Decrypt(byte[] encrypted)
    {
        byte[] raw = ForzaZip.DecryptContainer(encrypted, Fh6Keys.Get("ProfileData").DataKey);
        if (raw.Length < 8) throw new InvalidDataException("ProfileData blob header is missing.");
        uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
        uint expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if (compressedSize == 0 || compressedSize > raw.Length - 8) throw new InvalidDataException("ProfileData compressed-size header is invalid.");
        using var input = new MemoryStream(raw, 8, checked((int)compressedSize), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = expectedSize <= int.MaxValue ? new MemoryStream((int)expectedSize) : new MemoryStream();
        zlib.CopyTo(output);
        byte[] result = output.ToArray();
        if (result.Length != expectedSize) throw new InvalidDataException($"ProfileData expanded to {result.Length:n0} bytes; expected {expectedSize:n0}.");
        return result;
    }

    public static byte[] Encrypt(byte[] payload, byte[] targetTemplate)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(payload);
        byte[] z = compressed.ToArray();
        byte[] raw = new byte[8 + z.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0, 4), checked((uint)z.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4, 4), checked((uint)payload.Length));
        Buffer.BlockCopy(z, 0, raw, 8, z.Length);
        var key = Fh6Keys.Get("ProfileData");
        return ForzaZip.EncryptContainer(raw, targetTemplate, key.DataKey, key.MacKey);
    }

    public static byte[] Swap(byte[] donorEncrypted, byte[] targetEncrypted)
    {
        byte[] donorPayload = DecryptSwapInput(donorEncrypted, "Donor");
        byte[] targetPayload = DecryptSwapInput(targetEncrypted, "Target");
        var donorAccount = LocateCanonicalAccount(donorPayload);
        var targetAccount = LocateCanonicalAccount(targetPayload);

        byte[] swappedPayload = (byte[])donorPayload.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(swappedPayload.AsSpan(donorAccount.Offset, 8), targetAccount.Xuid);

        byte[] swapped = Encrypt(swappedPayload, targetEncrypted);
        var key = Fh6Keys.Get("ProfileData");
        if (!ForzaZip.ValidateContainer(swapped, key.DataKey, key.MacKey, out _))
            throw new CryptographicException("Swapped save failed container authentication.");
        byte[] check = Decrypt(swapped);
        if (!check.AsSpan().SequenceEqual(swappedPayload)) throw new CryptographicException("Swapped save failed payload verification.");
        if (LocateCanonicalAccount(check).Xuid != targetAccount.Xuid) throw new CryptographicException("Swapped save failed account verification.");
        return swapped;
    }

    private static (int Offset, ulong Xuid) LocateCanonicalAccount(byte[] payload)
    {
        // Account identity belongs to the binary career section's header, not to
        // any record. ActivationLocationSaveState need not be the first record.
        // Parse the complete framed section and require an exact round trip;
        // never guess an account field from a matching name elsewhere in a save.
        var container = ProfileContainer.Parse(payload);
        byte[] binary = container.Sections[ProfileContainer.BinaryIndex].Payload;
        BinaryCareer career;
        try
        {
            career = BinaryCareer.Parse(binary, hasXuid: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("ProfileData career account header could not be safely parsed. No swapped save was written.", ex);
        }

        if (!career.RoundTripsCleanly(binary) ||
            career.Xuid is < 100_000_000_000_000UL or > 9_999_999_999_999_999UL)
            throw new InvalidDataException("ProfileData career account header is invalid or unsupported. No swapped save was written.");

        int xuidOffset = 0;
        for (int index = 0; index < ProfileContainer.BinaryIndex; index++)
            xuidOffset = checked(xuidOffset + 8 + container.Sections[index].Payload.Length);
        // Binary section frame [marker, size], then [recordCount, account XUID].
        xuidOffset = checked(xuidOffset + 8 + 4);
        if (BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(xuidOffset, 8)) != career.Xuid)
            throw new InvalidDataException("ProfileData career account offset failed verification. No swapped save was written.");
        return (xuidOffset, career.Xuid);
    }

    private static byte[] DecryptSwapInput(byte[] encrypted, string label)
    {
        if (Fh5ProfileCrypto.HasHeaderAuthentication(encrypted.Length, encrypted))
            throw new InvalidDataException($"{label} is an FH5 save. Save Swap currently supports FH6 only. No swapped save was written.");
        var key = Fh6Keys.Get("ProfileData");
        if (!ForzaZip.ValidateContainer(encrypted, key.DataKey, key.MacKey, out _))
            throw new InvalidDataException($"{label} is not an authenticated encrypted FH6 C_ProfileData save. No swapped save was written.");
        return Decrypt(encrypted);
    }
}
