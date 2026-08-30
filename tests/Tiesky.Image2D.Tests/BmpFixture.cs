using System.Buffers.Binary;

internal static class BmpFixture
{
    public static byte[] Encode24(RawImage image)
    {
        int stride = ((image.Width * 3 + 3) / 4) * 4;
        byte[] bmp = new byte[54 + stride * image.Height];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        for (int sourceY = 0; sourceY < image.Height; sourceY++)
        {
            int targetY = image.Height - 1 - sourceY;
            for (int x = 0; x < image.Width; x++)
            {
                int source = (sourceY * image.Width + x) * 4;
                int target = 54 + targetY * stride + x * 3;
                bmp[target] = image.Pixels[source + 2];
                bmp[target + 1] = image.Pixels[source + 1];
                bmp[target + 2] = image.Pixels[source];
            }
        }

        return bmp;
    }

    public static byte[] EncodeIndexed()
    {
        const int Width = 4;
        const int Height = 2;
        const int PixelOffset = 54 + 16;
        byte[] bmp = new byte[PixelOffset + 8];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), PixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(46), 4);
        byte[] palette = [0,0,255,0, 0,255,0,0, 255,0,0,0, 255,255,255,0];
        palette.CopyTo(bmp, 54);
        byte[] bottom = [3,2,1,0];
        byte[] top = [0,1,2,3];
        bottom.CopyTo(bmp, PixelOffset);
        top.CopyTo(bmp, PixelOffset + 4);
        return bmp;
    }
}
