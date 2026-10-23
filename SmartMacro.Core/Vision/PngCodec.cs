using System.Buffers.Binary;
using System.IO.Compression;

namespace SmartMacro.Core.Vision;

/// <summary>
/// A tiny PNG reader / writer for the small pictures SmartMacro keeps inside macros (the "what you clicked" thumbnails
/// of Smart Click Snapshots). Core has no WPF / System.Drawing, so this is done by hand:
/// - Writing: 8-bit RGB (no alpha, because GDI screen captures leave alpha at 0, which would make the picture
///   invisible), filter "None" on every row, zlib-compressed. Any PNG viewer can open the result.
/// - Reading: 8-bit greyscale, grey + alpha, RGB and RGBA, non-interlaced, all five row filters. That covers what this
///   class writes and what normal tools write for screenshots. Anything else is refused with a plain message.
/// Every chunk's CRC is checked, sizes are limited, and nothing is trusted before it's checked.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Largest width / height accepted when reading (macro thumbnails are 56 px; templates at most 400).</summary>
    public const int MaxDimension = 2_000;

    /// <summary>Largest PNG file accepted when reading.</summary>
    public const int MaxFileBytes = 8 * 1024 * 1024;

    // ---------------- Writing ----------------

    /// <summary>Encodes the picture as an 8-bit RGB PNG (alpha is dropped).</summary>
    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentException("The picture is empty.", nameof(image));

        var width = image.Width;
        var height = image.Height;
        var raw = new byte[(width * 3 + 1) * height];
        var src = image.Pixels;
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: None
            var i = y * width * 4;
            for (var x = 0; x < width; x++, i += 4)
            {
                raw[o++] = src[i + 2]; // R
                raw[o++] = src[i + 1]; // G
                raw[o++] = src[i];     // B
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            compressed = buffer.ToArray();
        }

        using var output = new MemoryStream(compressed.Length + 64);
        output.Write(Signature, 0, Signature.Length);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: RGB
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>Encodes the picture as a PNG and returns it as Base64 text (for storing inside a macro file).</summary>
    public static string EncodeBase64(BgraImage image) => Convert.ToBase64String(Encode(image));

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        output.Write(four);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        output.Write(typeBytes, 0, 4);
        output.Write(data, 0, data.Length);

        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        output.Write(four);
    }

    // ---------------- Reading ----------------

    /// <summary>Decodes Base64 PNG text (as stored in a macro). False with a reason when it isn't a usable picture.</summary>
    public static bool TryDecodeBase64(string? base64, out BgraImage? image, out string? error)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(base64))
        {
            error = "There is no picture.";
            return false;
        }

        // Base64 is 4/3 the size of the data: refuse absurdly long text before allocating anything.
        if (base64.Length > MaxFileBytes / 3 * 4 + 4)
        {
            error = "The picture is too big.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            error = "The picture data is damaged (not valid Base64).";
            return false;
        }

        return TryDecode(bytes, out image, out error);
    }

    /// <summary>Decodes a PNG file's bytes into a BGRA picture (alpha 255 unless the PNG has its own alpha).</summary>
    public static bool TryDecode(byte[]? png, out BgraImage? image, out string? error)
    {
        image = null;
        error = null;
        try
        {
            image = Decode(png);
            return true;
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or IOException)
        {
            error = "The picture data is damaged: " + ex.Message;
            return false;
        }
    }

    private static BgraImage Decode(byte[]? png)
    {
        if (png is null || png.Length < Signature.Length + 12) throw new InvalidDataException("The picture data is too short to be a PNG.");
        if (png.Length > MaxFileBytes) throw new InvalidDataException("The picture is too big.");
        if (!png.AsSpan(0, Signature.Length).SequenceEqual(Signature)) throw new InvalidDataException("The picture isn't a PNG.");

        int width = 0, height = 0, bitDepth = 0, colourType = -1, interlace = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var idat = new MemoryStream();

        var pos = Signature.Length;
        while (pos + 12 <= png.Length && !sawEnd)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos, 4));
            if (length < 0 || pos + 12L + length > png.Length) throw new InvalidDataException("The picture data is cut off.");
            var typeSpan = png.AsSpan(pos + 4, 4);
            var type = System.Text.Encoding.ASCII.GetString(typeSpan);
            var data = png.AsSpan(pos + 8, length);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length, 4));
            var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeSpan), data) ^ 0xFFFFFFFFu;
            if (crc != storedCrc) throw new InvalidDataException("The picture data is damaged (checksum mismatch).");

            switch (type)
            {
                case "IHDR":
                    if (length != 13) throw new InvalidDataException("The picture header is damaged.");
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colourType = data[9];
                    interlace = data[12];
                    if (data[10] != 0 || data[11] != 0) throw new InvalidDataException("The picture uses an unknown compression.");
                    sawHeader = true;
                    break;
                case "IDAT":
                    if (!sawHeader) throw new InvalidDataException("The picture data is out of order.");
                    idat.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
                // Other chunks (gamma, text, physical size…) don't change the pixels we need.
            }

            pos += 12 + length;
        }

        if (!sawHeader) throw new InvalidDataException("The picture has no header.");
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException($"The picture size {width}×{height} isn't supported.");
        if (bitDepth != 8) throw new InvalidDataException("Only 8-bit PNG pictures are supported.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNG pictures aren't supported.");
        var channels = colourType switch
        {
            0 => 1, // grey
            2 => 3, // RGB
            4 => 2, // grey + alpha
            6 => 4, // RGBA
            _ => throw new InvalidDataException("Only greyscale, RGB and RGBA PNG pictures are supported."),
        };

        var stride = width * channels;
        var expected = (long)(stride + 1) * height;
        var raw = new byte[expected];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = zlib.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }

            if (read != raw.Length) throw new InvalidDataException("The picture data is cut off.");
        }

        Unfilter(raw, stride, height, channels);

        var image = BgraImage.Create(width, height);
        var dst = image.Pixels;
        for (var y = 0; y < height; y++)
        {
            var s = y * (stride + 1) + 1;
            var d = y * width * 4;
            for (var x = 0; x < width; x++, s += channels, d += 4)
            {
                switch (channels)
                {
                    case 1:
                        dst[d] = dst[d + 1] = dst[d + 2] = raw[s];
                        dst[d + 3] = 255;
                        break;
                    case 2:
                        dst[d] = dst[d + 1] = dst[d + 2] = raw[s];
                        dst[d + 3] = raw[s + 1];
                        break;
                    case 3:
                        dst[d] = raw[s + 2];
                        dst[d + 1] = raw[s + 1];
                        dst[d + 2] = raw[s];
                        dst[d + 3] = 255;
                        break;
                    default:
                        dst[d] = raw[s + 2];
                        dst[d + 1] = raw[s + 1];
                        dst[d + 2] = raw[s];
                        dst[d + 3] = raw[s + 3];
                        break;
                }
            }
        }

        return image;
    }

    /// <summary>Undoes the PNG row filters in place (each row starts with its filter byte).</summary>
    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            var filter = raw[row];
            var cur = row + 1;
            var prev = y == 0 ? -1 : row - stride; // start of the previous row's pixels
            switch (filter)
            {
                case 0:
                    break;
                case 1: // Sub
                    for (var i = bpp; i < stride; i++) raw[cur + i] = (byte)(raw[cur + i] + raw[cur + i - bpp]);
                    break;
                case 2: // Up
                    if (prev < 0) break;
                    for (var i = 0; i < stride; i++) raw[cur + i] = (byte)(raw[cur + i] + raw[prev + i]);
                    break;
                case 3: // Average
                    for (var i = 0; i < stride; i++)
                    {
                        var left = i >= bpp ? raw[cur + i - bpp] : 0;
                        var up = prev >= 0 ? raw[prev + i] : 0;
                        raw[cur + i] = (byte)(raw[cur + i] + ((left + up) >> 1));
                    }

                    break;
                case 4: // Paeth
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? raw[cur + i - bpp] : 0;
                        int b = prev >= 0 ? raw[prev + i] : 0;
                        int c = prev >= 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                        raw[cur + i] = (byte)(raw[cur + i] + Paeth(a, b, c));
                    }

                    break;
                default:
                    throw new InvalidDataException("The picture data is damaged (unknown row filter).");
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>CRC-32 (the PNG / zip polynomial), table-driven.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
