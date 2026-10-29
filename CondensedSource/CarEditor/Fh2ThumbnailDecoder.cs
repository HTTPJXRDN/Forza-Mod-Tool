using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace FH6CarEditor;

/// <summary>Native FH2 Xbox BC7 previews; fixed layout maps avoid a runtime XG dependency.</summary>
internal static class Fh2ThumbnailDecoder
{
    static readonly Lazy<ushort[]> Big = new(() => ReadMap(816, 459, 524288));
    static readonly Lazy<ushort[]> Small = new(() => ReadMap(192, 108, 32768));

    internal static byte[] LinearBlocks(byte[] bytes, out int width, out int height, out bool premultiplied)
    {
        if (bytes.Length < 83) throw new InvalidDataException("Incomplete FH2 thumbnail.");
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        int H(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        width = H(64); height = H(66); uint flags = U(79);
        int payload = (width, height) switch { (816, 459) => 524288, (192, 108) => 32768,
            _ => throw new InvalidDataException("Unsupported FH2 thumbnail dimensions.") };
        if (!bytes.AsSpan(0, 4).SequenceEqual("burG"u8) || U(4) != 65537 || U(8) != 83 || U(12) != bytes.Length ||
            !bytes.AsSpan(16, 4).SequenceEqual("BCXT"u8) || U(20) != 65538 || U(24) != 40 || U(28) != 83 ||
            U(32) != payload || U(36) != payload || bytes.Length != 83 + payload || !bytes.AsSpan(40, 4).SequenceEqual("HCXT"u8) ||
            H(68) != 1 || H(70) != width || H(72) != height || H(74) != 1 || bytes[76] != 1 || bytes[77] != 1 || bytes[78] != 0 ||
            (flags & 31) != 14 || ((flags >> 5) & 63) != 9 || ((flags >> 11) & 63) != 0 || (flags & (3u << 25)) != 0)
            throw new InvalidDataException("Unsupported or incomplete FH2 thumbnail layout.");
        ushort[] map = width == 816 ? Big.Value : Small.Value;
        byte[] linear = new byte[checked(map.Length * 16)];
        for (int i = 0; i < map.Length; i++) Buffer.BlockCopy(bytes, 83 + map[i] * 16, linear, i * 16, 16);
        premultiplied = (flags & (1u << 27)) != 0;
        return linear;
    }

    internal static BitmapSource Decode(byte[] bytes)
    {
        byte[] linear = LinearBlocks(bytes, out int width, out int height, out bool premultiplied);
        var decoder = new BcDecoder(); decoder.Options.IsParallel = false;
        using var stream = new MemoryStream(linear, false);
        var rgba = decoder.DecodeRaw(stream, width, height, CompressionFormat.Bc7);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < rgba.Length; i++)
        {
            pixels[i * 4] = rgba[i].b; pixels[i * 4 + 1] = rgba[i].g;
            pixels[i * 4 + 2] = rgba[i].r; pixels[i * 4 + 3] = rgba[i].a;
        }
        var image = BitmapSource.Create(width, height, 96, 96, premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }

    static ushort[] ReadMap(int width, int height, int payload)
    {
        int count = checked(((width + 3) / 4) * ((height + 3) / 4));
        using var source = typeof(Fh2ThumbnailDecoder).Assembly.GetManifestResourceStream($"FH6LocalCryptoTool.Thumbnails.fh2-bc7-{width}x{height}-tile14.map.gz")
            ?? throw new InvalidDataException("FH2 thumbnail layout map is missing.");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        byte[] bytes = new byte[count * 2]; gzip.ReadExactly(bytes);
        if (gzip.ReadByte() != -1) throw new InvalidDataException("Invalid FH2 thumbnail layout map size.");
        var result = new ushort[count]; var seen = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
            if (result[i] >= payload / 16 || !seen.Add(result[i])) throw new InvalidDataException("Invalid FH2 thumbnail layout map.");
        }
        return result;
    }
}
