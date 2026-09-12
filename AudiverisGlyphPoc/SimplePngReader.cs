using System.IO.Compression;
using System.Text;

namespace AudiverisGlyphPoc;

/// <summary>
/// Minimal PNG reader for the grayscale 8-bit PNG files emitted by SvgScene.
/// It intentionally supports only the exact format produced by SimplePngWriter:
/// no interlace, grayscale color type 0, filter type 0.
/// </summary>
public static class SimplePngReader
{
    private static readonly byte[] Signature =
    [
        137, 80, 78, 71, 13, 10, 26, 10
    ];

    public static GrayscaleImage Read(string path)
    {
        using var stream = File.OpenRead(path);

        var signature = new byte[Signature.Length];
        ReadExactly(stream, signature);

        if (!signature.SequenceEqual(Signature))
        {
            throw new InvalidDataException($"'{path}' is not a PNG file.");
        }

        int? width = null;
        int? height = null;
        var idat = new MemoryStream();

        while (stream.Position < stream.Length)
        {
            var length = ReadUInt32BigEndian(stream);
            var typeBytes = new byte[4];
            ReadExactly(stream, typeBytes);
            var type = Encoding.ASCII.GetString(typeBytes);

            var data = new byte[length];
            ReadExactly(stream, data);

            // CRC is intentionally not revalidated here. The exporter generated it,
            // and malformed data will fail during zlib decompression anyway.
            _ = ReadUInt32BigEndian(stream);

            switch (type)
            {
                case "IHDR":
                    if (data.Length != 13)
                    {
                        throw new InvalidDataException("Invalid PNG IHDR length.");
                    }

                    width = checked((int)ReadUInt32BigEndian(data, 0));
                    height = checked((int)ReadUInt32BigEndian(data, 4));

                    var bitDepth = data[8];
                    var colorType = data[9];
                    var interlace = data[12];

                    if (bitDepth != 8 || colorType != 0 || interlace != 0)
                    {
                        throw new NotSupportedException(
                            "Only non-interlaced 8-bit grayscale PNG files are supported.");
                    }

                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    return Decode(width, height, idat.ToArray());
            }
        }

        throw new InvalidDataException("PNG file has no IEND chunk.");
    }

    private static GrayscaleImage Decode(
        int? width,
        int? height,
        byte[] compressed)
    {
        if (width is null || height is null)
        {
            throw new InvalidDataException("PNG file has no IHDR chunk.");
        }

        using var source = new MemoryStream(compressed);
        using var zlib = new ZLibStream(
            source,
            CompressionMode.Decompress);
        using var raw = new MemoryStream();

        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var rowSize = width.Value + 1;
        var expected = checked(height.Value * rowSize);

        if (bytes.Length != expected)
        {
            throw new InvalidDataException(
                $"Unexpected PNG payload size {bytes.Length}; expected {expected}.");
        }

        var pixels = new byte[checked(width.Value * height.Value)];

        for (var y = 0; y < height.Value; y++)
        {
            var rowOffset = y * rowSize;
            var filter = bytes[rowOffset];

            if (filter != 0)
            {
                throw new NotSupportedException(
                    $"PNG filter {filter} is not supported by the PoC reader.");
            }

            Buffer.BlockCopy(
                bytes,
                rowOffset + 1,
                pixels,
                y * width.Value,
                width.Value);
        }

        return new GrayscaleImage(
            width.Value,
            height.Value,
            pixels);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        var buffer = new byte[4];
        ReadExactly(stream, buffer);
        return ReadUInt32BigEndian(buffer, 0);
    }

    private static uint ReadUInt32BigEndian(
        IReadOnlyList<byte> buffer,
        int offset)
    {
        return ((uint)buffer[offset] << 24)
            | ((uint)buffer[offset + 1] << 16)
            | ((uint)buffer[offset + 2] << 8)
            | buffer[offset + 3];
    }

    private static void ReadExactly(
        Stream stream,
        byte[] buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = stream.Read(
                buffer,
                offset,
                buffer.Length - offset);

            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}

public sealed record GrayscaleImage(
    int Width,
    int Height,
    byte[] Pixels)
{
    public BinaryGlyph ToBinaryGlyph(byte threshold = 128)
    {
        var x = new List<int>();
        var y = new List<int>();

        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                if (Pixels[row * Width + column] < threshold)
                {
                    x.Add(column);
                    y.Add(row);
                }
            }
        }

        if (x.Count == 0)
        {
            throw new InvalidDataException("PNG contains no foreground pixels.");
        }

        return new BinaryGlyph(x, y);
    }
}
