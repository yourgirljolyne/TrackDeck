using System.Buffers.Binary;

namespace TrackDeck;

/// <summary>File ids and formats used by the keyboard's screen (from Corsair Web Hub).</summary>
public static class ScreenFiles
{
    public const ushort ResourceMap = 61;   // list of image resources the firmware knows about
    public const ushort ActiveScreen = 62;  // copy of the screen config currently shown

    // Our own two files, at the top of Web Hub's user range (28000-32767) to avoid its picks.
    public const ushort OurConfig = 32700;
    public const ushort OurImage = 32701;

    public const byte ConfigTypeImage = 56;     // "screen resource config"
    public const byte ConfigTypeGif = 102;

    public const int Width = 248, Height = 170;

    /// <summary>16-byte screen config pointing at an image resource.</summary>
    public static byte[] ImageConfig(ushort resourceId)
    {
        var b = new byte[16];
        b[0] = ConfigTypeImage;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), resourceId);
        return b;
    }

    /// <summary>Adds a resource id to the resource map file if missing. Returns null if unchanged.</summary>
    public static byte[]? AddToResourceMap(byte[] map, ushort resourceId)
    {
        if (map.Length < 4) map = [0, 0, 0, 0];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(2));
        var items = new List<byte[]>();
        for (int i = 0; i < count && 4 + i * 8 + 8 <= map.Length; i++)
        {
            var item = map.AsSpan(4 + i * 8, 8).ToArray();
            if (BinaryPrimitives.ReadUInt16LittleEndian(item) == 0) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(item) == resourceId) return null;
            items.Add(item);
        }
        var add = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(add, resourceId);
        BinaryPrimitives.WriteUInt16LittleEndian(add.AsSpan(2), resourceId);
        items.Add(add);

        var result = new byte[4 + items.Count * 8];
        map.AsSpan(0, 2).CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)items.Count);
        for (int i = 0; i < items.Count; i++) items[i].CopyTo(result, 4 + i * 8);
        return result;
    }

    /// <summary>
    /// Builds the image package exactly like Web Hub's Air 99 ScreenService.convertToBMP:
    /// 0x48 0x00, then a 16-bit BMP header, RGB565 pixels from offset 54 (rows top to bottom), unix time at the end.
    /// </summary>
    public static byte[] ImagePackage(ReadOnlySpan<byte> rgba, int w, int h)
    {
        int rowBytes = w * 2, pad = (4 - rowBytes % 4) % 4, stride = rowBytes + pad;
        int pixelBytes = stride * h, dataOffset = 54, total = dataOffset + pixelBytes + 6;
        var b = new byte[total];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 72);
        BinaryPrimitives.WriteUInt16LittleEndian(s[1..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 19778); // "BM"
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], h);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)pixelBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2835);
        // (Web Hub also writes colour masks at 48..67; pixel data then overwrites them from 54.)
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0);

        int o = dataOffset;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int r = rgba[i], g = rgba[i + 1], bl = rgba[i + 2];
                ushort px = (ushort)(((r >> 3) & 31) << 11 | ((g >> 2) & 63) << 5 | ((bl >> 3) & 31));
                BinaryPrimitives.WriteUInt16LittleEndian(s[o..], px);
                o += 2;
            }
            o += pad;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[(total - 4)..], (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return b;
    }
}
