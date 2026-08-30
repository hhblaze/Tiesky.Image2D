using System.Buffers.Binary;
using System.IO.Compression;

internal static class PngFixture
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(RawImage image)
    {
        using MemoryStream output = new();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(image.Pixels.AsSpan(y * image.Width * 4, image.Width * 4));
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    public static byte[] EncodeRgb(RawImage image, int filter, int idatChunkSize = int.MaxValue)
    {
        if ((uint)filter > 4) throw new ArgumentOutOfRangeException(nameof(filter));
        using MemoryStream output = new();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR"u8, header);

        int rowLength = checked(image.Width * 3);
        byte[] current = new byte[rowLength];
        byte[] previous = new byte[rowLength];
        byte[] filtered = new byte[rowLength];
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                int source = y * image.Width * 4;
                for (int x = 0, target = 0; x < image.Width; x++, source += 4, target += 3)
                {
                    current[target] = image.Pixels[source];
                    current[target + 1] = image.Pixels[source + 1];
                    current[target + 2] = image.Pixels[source + 2];
                }

                for (int x = 0; x < rowLength; x++)
                {
                    int left = x >= 3 ? current[x - 3] : 0;
                    int above = y == 0 ? 0 : previous[x];
                    int upperLeft = y == 0 || x < 3 ? 0 : previous[x - 3];
                    int predictor = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => above,
                        3 => (left + above) >> 1,
                        _ => Paeth(left, above, upperLeft),
                    };
                    filtered[x] = unchecked((byte)(current[x] - predictor));
                }

                zlib.WriteByte((byte)filter);
                zlib.Write(filtered);
                (current, previous) = (previous, current);
            }
        }

        byte[] payload = compressed.ToArray();
        for (int start = 0; start < payload.Length; start += idatChunkSize)
        {
            WriteChunk(output, "IDAT"u8, payload.AsSpan(start, Math.Min(idatChunkSize, payload.Length - start)));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    public static RawImage Decode(ReadOnlySpan<byte> png)
    {
        if (!png[..8].SequenceEqual(Signature))
        {
            throw new InvalidOperationException("Invalid PNG output signature.");
        }

        int width = 0;
        int height = 0;
        int colorType = 0;
        List<byte> compressed = [];
        int offset = 8;
        while (offset < png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]));
            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[4..]));
                colorType = payload[9];
                if (payload[8] != 8 || colorType is not (2 or 6) || payload[12] != 0)
                {
                    throw new InvalidOperationException("Test reader requires 8-bit non-interlaced RGB/RGBA output.");
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.AddRange(payload.ToArray());
            }

            offset += length + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        byte[] pixels = new byte[width * height * 4];
        int bytesPerPixel = colorType == 2 ? 3 : 4;
        byte[] previous = new byte[width * bytesPerPixel];
        byte[] current = new byte[width * bytesPerPixel];
        using MemoryStream input = new(compressed.ToArray());
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte();
            zlib.ReadExactly(current);
            Unfilter(current, previous, bytesPerPixel, filter);
            if (colorType == 6)
            {
                current.CopyTo(pixels, y * current.Length);
            }
            else
            {
                int output = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int inputOffset = x * 3;
                    pixels[output++] = current[inputOffset];
                    pixels[output++] = current[inputOffset + 1];
                    pixels[output++] = current[inputOffset + 2];
                    pixels[output++] = 255;
                }
            }
            (current, previous) = (previous, current);
        }

        return new RawImage(width, height, pixels);
    }

    public static int GetColorType(ReadOnlySpan<byte> png) => png[25];

    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter)
    {
        for (int x = 0; x < row.Length; x++)
        {
            int left = x >= bpp ? row[x - bpp] : 0;
            int above = previous[x];
            int upperLeft = x >= bpp ? previous[x - bpp] : 0;
            int predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => above,
                3 => (left + above) >> 1,
                4 => Paeth(left, above, upperLeft),
                _ => throw new InvalidOperationException("Invalid output filter."),
            };
            row[x] = unchecked((byte)(row[x] + predictor));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        type.CopyTo(header[4..]);
        destination.Write(header);
        destination.Write(payload);
        uint crc = Crc(type, payload);
        Span<byte> suffix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(suffix, crc);
        destination.Write(suffix);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = Update(crc, value);
        }

        foreach (byte value in payload)
        {
            crc = Update(crc, value);
        }

        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
