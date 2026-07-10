using System;
using System.IO;
using System.IO.Compression;

namespace SaS2Resalter;

/// <summary>
/// Minimal, dependency-free PNG decoder producing straight-alpha 8-bit RGBA.
///
/// We decode PNGs ourselves rather than using MonoGame's Texture2D.FromStream because that path
/// pulls in SharpDX.MediaFoundation/Mathematics, which fails to load under the game's Wine/.NET
/// setup (see LogOutput.log: "Could not load file or assembly 'SharpDX.Mathematics'"). Decoding
/// here and uploading via Texture2D.SetData avoids that code path entirely.
///
/// Supports 8-bit non-interlaced PNGs in grayscale (0), RGB (2), palette (3) and RGBA (6) color
/// types, with optional tRNS. The editor writes 8-bit RGBA, which is the primary case.
/// </summary>
internal static class PngDecoder
{
    public sealed class Image
    {
        public int Width;
        public int Height;
        public byte[] Rgba; // length = Width * Height * 4, straight (non-premultiplied) alpha
    }

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static Image Decode(byte[] data)
    {
        if (data == null || data.Length < 8)
            throw new InvalidDataException("PNG too small");
        for (var i = 0; i < 8; i++)
            if (data[i] != Signature[i])
                throw new InvalidDataException("Not a PNG (bad signature)");

        var pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        byte[] palette = null; // RGB triples
        byte[] trns = null;    // palette alpha, or grayscale/RGB key (we only use palette alpha)

        while (pos + 8 <= data.Length)
        {
            var len = ReadBE32(data, pos);
            pos += 4;
            var type = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;
            if (len < 0 || pos + len + 4 > data.Length)
                throw new InvalidDataException("Corrupt PNG chunk");

            switch (type)
            {
                case "IHDR":
                    width = ReadBE32(data, pos);
                    height = ReadBE32(data, pos + 4);
                    bitDepth = data[pos + 8];
                    colorType = data[pos + 9];
                    interlace = data[pos + 12];
                    break;
                case "PLTE":
                    palette = new byte[len];
                    Array.Copy(data, pos, palette, 0, len);
                    break;
                case "tRNS":
                    trns = new byte[len];
                    Array.Copy(data, pos, trns, 0, len);
                    break;
                case "IDAT":
                    idat.Write(data, pos, len);
                    break;
            }

            pos += len + 4; // skip data + CRC
            if (type == "IEND") break;
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Invalid PNG dimensions");
        if (bitDepth != 8)
            throw new NotSupportedException($"Unsupported PNG bit depth {bitDepth} (only 8 is supported)");
        if (interlace != 0)
            throw new NotSupportedException("Interlaced PNGs are not supported");

        var channels = colorType switch
        {
            0 => 1, // grayscale
            2 => 3, // RGB
            3 => 1, // palette index
            4 => 2, // grayscale + alpha
            6 => 4, // RGBA
            _ => throw new NotSupportedException($"Unsupported PNG color type {colorType}")
        };

        var raw = Inflate(idat.ToArray());
        var stride = width * channels;
        var expected = (stride + 1) * height;
        if (raw.Length < expected)
            throw new InvalidDataException($"PNG data short: {raw.Length} < {expected}");

        var unfiltered = Unfilter(raw, width, height, channels);
        var rgba = ToRgba(unfiltered, width, height, colorType, channels, palette, trns);
        return new Image { Width = width, Height = height, Rgba = rgba };
    }

    private static int ReadBE32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    /// Inflate a zlib stream (skip the 2-byte zlib header; DeflateStream handles the raw deflate
    /// body and ignores the trailing adler32 checksum).
    private static byte[] Inflate(byte[] zlib)
    {
        if (zlib.Length < 2)
            throw new InvalidDataException("Empty IDAT");
        using var input = new MemoryStream(zlib, 2, zlib.Length - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// Reverse PNG scanline filtering in place, returning width*height*channels bytes.
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var bpp = channels; // bytes per pixel (8-bit)
        var outBuf = new byte[stride * height];
        var srcPos = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = raw[srcPos++];
            var rowStart = y * stride;
            var prevRowStart = rowStart - stride;

            for (var x = 0; x < stride; x++)
            {
                int value = raw[srcPos++];
                var a = x >= bpp ? outBuf[rowStart + x - bpp] : 0;
                var b = y > 0 ? outBuf[prevRowStart + x] : 0;
                var c = (y > 0 && x >= bpp) ? outBuf[prevRowStart + x - bpp] : 0;

                int recon = filter switch
                {
                    0 => value,
                    1 => value + a,
                    2 => value + b,
                    3 => value + ((a + b) >> 1),
                    4 => value + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}")
                };
                outBuf[rowStart + x] = (byte)(recon & 0xFF);
            }
        }

        return outBuf;
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

    private static byte[] ToRgba(byte[] px, int width, int height, int colorType, int channels,
        byte[] palette, byte[] trns)
    {
        var outBuf = new byte[width * height * 4];
        var count = width * height;

        for (var i = 0; i < count; i++)
        {
            var s = i * channels;
            byte r, g, b, a;
            switch (colorType)
            {
                case 0: // grayscale
                    r = g = b = px[s];
                    a = 255;
                    break;
                case 2: // RGB
                    r = px[s];
                    g = px[s + 1];
                    b = px[s + 2];
                    a = 255;
                    break;
                case 3: // palette
                {
                    var idx = px[s];
                    var p3 = idx * 3;
                    if (palette != null && p3 + 2 < palette.Length)
                    {
                        r = palette[p3];
                        g = palette[p3 + 1];
                        b = palette[p3 + 2];
                    }
                    else
                    {
                        r = g = b = 0;
                    }
                    a = (trns != null && idx < trns.Length) ? trns[idx] : (byte)255;
                    break;
                }
                case 4: // grayscale + alpha
                    r = g = b = px[s];
                    a = px[s + 1];
                    break;
                default: // 6 = RGBA
                    r = px[s];
                    g = px[s + 1];
                    b = px[s + 2];
                    a = px[s + 3];
                    break;
            }

            var o = i * 4;
            outBuf[o] = r;
            outBuf[o + 1] = g;
            outBuf[o + 2] = b;
            outBuf[o + 3] = a;
        }

        return outBuf;
    }
}
