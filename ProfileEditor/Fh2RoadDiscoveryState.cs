using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>
/// Experimental FH2 mainland/island bitmaps, each with its own map identity.
/// This is not the FH6 RoadDiscoveryBrio layout.
/// </summary>
public static class Fh2RoadDiscoveryState
{
    public const uint RoadCount = 315;
    public const uint MapId = 0xFA38F25E;
    public const uint IslandRoadCount = 50;
    public const uint IslandMapId = 0x9A790429;
    public const int BitmapBytes = 4096;
    public const int BitmapCapacity = BitmapBytes * 8;
    const int PayloadBytes = 4 + BitmapBytes + 4;
    static readonly (string Name, string Serializer, uint Marker)[] Records =
    {
        ("ChampionshipDrivatarsState", "ChampionshipDrivatarsState", 1),
        ("GameDVRState", "GameDVRState", 0),
        ("IslandChampionshipDrivatarsState", "ChampionshipDrivatarsState", 1),
        ("RadioState", "RadioState", 1),
        ("RoadDiscovery", "RoadDiscoveryState", 1),
        ("RoadDiscoveryIsland", "RoadDiscoveryState", 1),
    };
    public readonly record struct Info(int BitmapOffset, int DiscoveredSamples);
    public static bool TryRead(byte[] data, out Info info, bool island = false)
    {
        info = default;
        try
        {
            int offset = 0, bitmap = -1;
            uint ReadUInt()
            {
                if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Truncated FH2 binary record.");
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)); offset += 4; return value;
            }
            string ReadString()
            {
                uint length = ReadUInt();
                if (length is 0 or > 128 || length > data.Length - offset) throw new InvalidDataException("Unsupported FH2 record name.");
                string text = new UTF8Encoding(false, true).GetString(data.AsSpan(offset, (int)length)); offset += (int)length; return text;
            }
            uint count = ReadUInt(); if (count is not (5 or 6)) return false;
            for (int index = 0; index < count; index++)
            {
                var expected = Records[index];
                if (ReadString() != expected.Name || ReadString() != expected.Serializer || ReadUInt() != expected.Marker) return false;
                // FH2 stores absolute endpoints relative to byte 4, not payload
                // lengths, FH6 name hashes, account IDs or trailing ordinals.
                uint endpoint = ReadUInt();
                if (endpoint > data.Length - 4) return false;
                int end = checked((int)endpoint + 4);
                if (end < offset) return false;
                if (expected.Name is "RoadDiscovery" or "RoadDiscoveryIsland")
                {
                    bool thisIsland = expected.Name == "RoadDiscoveryIsland";
                    if (end - offset != PayloadBytes || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) != (thisIsland ? IslandMapId : MapId) ||
                        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end - 4, 4)) != (thisIsland ? 5U : 4U)) return false;
                    if (thisIsland == island) bitmap = offset + 4;
                }
                offset = end;
            }
            if (offset != data.Length || bitmap < 0) return false;
            int samples = 0; for (int i = 0; i < BitmapBytes; i++) samples += BitOperations.PopCount((uint)data[bitmap + i]);
            info = new(bitmap, samples); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return false; }
    }
    public static byte[] DiscoverAll(byte[] data, bool island = false)
    {
        if (!TryRead(data, out var before, island)) throw new InvalidDataException("Unsupported FH2 road-discovery layout; nothing was changed.");
        byte[] edited = (byte[])data.Clone();
        // This is the recognized fixed bitmap capacity, not 32,768 distinct
        // roads. Retain map identity, the trailing word, endpoints and every
        // other record. Acceptance/unused-bit behavior needs an in-game test.
        edited.AsSpan(before.BitmapOffset, BitmapBytes).Fill(0xFF);
        if (!TryRead(edited, out var after, island) || after.BitmapOffset != before.BitmapOffset || after.DiscoveredSamples != BitmapCapacity)
            throw new InvalidDataException("FH2 road bitmap verification failed.");
        return edited;
    }
}
