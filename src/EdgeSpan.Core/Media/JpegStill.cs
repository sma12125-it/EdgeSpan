namespace EdgeSpan.Media;

/// <summary>
/// Baseline JPEG (4:2:0) from tightly packed BGRA. Android decodes it with
/// BitmapFactory. Flag bit 0 is the keyframe bit the H.264 path already uses.
/// Flag bit 1 marks JPEG so a decoder does not hand the bytes to MediaCodec.
/// </summary>
public static class JpegStill
{
    public const byte Flag = 0x03;

    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality = 72)
    {
        if (width < 2 || height < 2)
            throw new ArgumentException("JPEG needs at least 2x2.");
        width &= ~1;
        height &= ~1;
        quality = Math.Clamp(quality, 30, 90);
        var yQ = Scale(LumaQ, quality);
        var cQ = Scale(ChromaQ, quality);
        var yDc = Codes(YdcBits, YdcVal);
        var yAc = Codes(YacBits, YacVal);
        var cDc = Codes(CdcBits, CdcVal);
        var cAc = Codes(CacBits, CacVal);
        var mw = (width + 15) & ~15;
        var mh = (height + 15) & ~15;
        using var stream = new MemoryStream(width * height / 2);
        void W(params byte[] bytes) => stream.Write(bytes);
        W(0xFF, 0xD8);
        W(0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00);
        WriteQuant(stream, 0, yQ);
        WriteQuant(stream, 1, cQ);
        W(0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01);
        WriteHuffman(stream, 0x00, YdcBits, YdcVal);
        WriteHuffman(stream, 0x10, YacBits, YacVal);
        WriteHuffman(stream, 0x01, CdcBits, CdcVal);
        WriteHuffman(stream, 0x11, CacBits, CacVal);
        W(0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00);
        var bits = new Bits(stream);
        var predY = 0;
        var predCb = 0;
        var predCr = 0;
        var block = new double[64];
        for (var my = 0; my < mh; my += 16)
        {
            for (var mx = 0; mx < mw; mx += 16)
            {
                for (var by = 0; by < 2; by++)
                {
                    for (var bx = 0; bx < 2; bx++)
                    {
                        FillLuma(bgra, stride, width, height, mx + bx * 8, my + by * 8, block);
                        EncodeBlock(bits, block, yQ, yDc, yAc, ref predY);
                    }
                }
                FillChroma(bgra, stride, width, height, mx, my, block, chroma: false);
                EncodeBlock(bits, block, cQ, cDc, cAc, ref predCb);
                FillChroma(bgra, stride, width, height, mx, my, block, chroma: true);
                EncodeBlock(bits, block, cQ, cDc, cAc, ref predCr);
            }
        }
        bits.Flush();
        stream.WriteByte(0xFF);
        stream.WriteByte(0xD9);
        return stream.ToArray();
    }

    private static void FillLuma(ReadOnlySpan<byte> bgra, int stride, int width, int height, int ox, int oy, double[] block)
    {
        var i = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                Sample(bgra, stride, width, height, ox + x, oy + y, out var b, out var g, out var r);
                block[i++] = (0.299 * r + 0.587 * g + 0.114 * b) - 128;
            }
        }
    }

    private static void FillChroma(ReadOnlySpan<byte> bgra, int stride, int width, int height, int ox, int oy, double[] block, bool chroma)
    {
        var i = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var r = 0.0;
                var g = 0.0;
                var b = 0.0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        Sample(bgra, stride, width, height, ox + x * 2 + dx, oy + y * 2 + dy, out var sb, out var sg, out var sr);
                        b += sb;
                        g += sg;
                        r += sr;
                    }
                }
                r /= 4;
                g /= 4;
                b /= 4;
                block[i++] = (chroma
                    ? 128 + 0.5 * r - 0.418688 * g - 0.081312 * b
                    : 128 - 0.168736 * r - 0.331264 * g + 0.5 * b) - 128;
            }
        }
    }

    private static void Sample(ReadOnlySpan<byte> bgra, int stride, int width, int height, int x, int y, out int b, out int g, out int r)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var i = y * stride + x * 4;
        b = i + 2 < bgra.Length ? bgra[i] : 0;
        g = i + 2 < bgra.Length ? bgra[i + 1] : 0;
        r = i + 2 < bgra.Length ? bgra[i + 2] : 0;
    }

    private static void EncodeBlock(Bits bits, double[] spatial, int[] quant, (ushort Code, byte Len)[] dc, (ushort Code, byte Len)[] ac, ref int pred)
    {
        Fdct(spatial);
        Span<int> zz = stackalloc int[64];
        for (var i = 0; i < 64; i++)
            zz[Zig[i]] = (int)Math.Round(spatial[i] / quant[i]);
        var diff = zz[0] - pred;
        pred = zz[0];
        var size = BitSize(Math.Abs(diff));
        bits.Write(dc[size].Code, dc[size].Len);
        if (size > 0)
            bits.Write(diff < 0 ? (Math.Abs(diff) ^ ((1 << size) - 1)) : Math.Abs(diff), size);
        var last = 63;
        while (last > 0 && zz[last] == 0)
            last--;
        var run = 0;
        for (var i = 1; i <= last; i++)
        {
            var v = zz[i];
            if (v == 0)
            {
                run++;
                continue;
            }
            while (run > 15)
            {
                bits.Write(ac[0xF0].Code, ac[0xF0].Len);
                run -= 16;
            }
            var mag = Math.Abs(v);
            var n = BitSize(mag);
            var sym = (run << 4) | n;
            bits.Write(ac[sym].Code, ac[sym].Len);
            bits.Write(v < 0 ? (mag ^ ((1 << n) - 1)) : mag, n);
            run = 0;
        }
        if (last < 63)
            bits.Write(ac[0].Code, ac[0].Len);
    }

    private static void Fdct(double[] block)
    {
        Span<double> tmp = stackalloc double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var x = 0; x < 8; x++)
                    sum += block[y * 8 + x] * Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                var c = u == 0 ? 1 / Math.Sqrt(2) : 1;
                tmp[y * 8 + u] = 0.5 * c * sum;
            }
        }
        for (var x = 0; x < 8; x++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0.0;
                for (var y = 0; y < 8; y++)
                    sum += tmp[y * 8 + x] * Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                var c = v == 0 ? 1 / Math.Sqrt(2) : 1;
                block[v * 8 + x] = 0.5 * c * sum;
            }
        }
    }

    private static int BitSize(int abs)
    {
        var n = 0;
        while (abs > 0)
        {
            n++;
            abs >>= 1;
        }
        return n;
    }

    private static int[] Scale(byte[] table, int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var q = new int[64];
        for (var i = 0; i < 64; i++)
            q[i] = Math.Clamp((table[i] * scale + 50) / 100, 1, 255);
        return q;
    }

    private static (ushort Code, byte Len)[] Codes(byte[] bits, byte[] values)
    {
        var table = new (ushort, byte)[256];
        var code = 0;
        var k = 0;
        for (var len = 1; len <= 16; len++)
        {
            for (var i = 0; i < bits[len - 1]; i++)
            {
                table[values[k++]] = ((ushort)code, (byte)len);
                code++;
            }
            code <<= 1;
        }
        return table;
    }

    private static void WriteQuant(Stream stream, byte id, int[] q)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(0xDB);
        stream.WriteByte(0x00);
        stream.WriteByte(67);
        stream.WriteByte(id);
        for (var i = 0; i < 64; i++)
            stream.WriteByte((byte)q[Order[i]]);
    }

    private static void WriteHuffman(Stream stream, byte cls, byte[] bits, byte[] values)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(0xC4);
        var len = 2 + 1 + 16 + values.Length;
        stream.WriteByte((byte)(len >> 8));
        stream.WriteByte((byte)len);
        stream.WriteByte(cls);
        stream.Write(bits);
        stream.Write(values);
    }

    private sealed class Bits
    {
        private readonly Stream _stream;
        private int _acc;
        private int _n;

        public Bits(Stream stream) => _stream = stream;

        public void Write(int code, int size)
        {
            _acc = (_acc << size) | (code & ((1 << size) - 1));
            _n += size;
            while (_n >= 8)
            {
                _n -= 8;
                var b = (_acc >> _n) & 0xFF;
                _stream.WriteByte((byte)b);
                if (b == 0xFF)
                    _stream.WriteByte(0);
            }
        }

        public void Flush()
        {
            if (_n <= 0)
                return;
            var b = ((_acc << (8 - _n)) | ((1 << (8 - _n)) - 1)) & 0xFF;
            _stream.WriteByte((byte)b);
            if (b == 0xFF)
                _stream.WriteByte(0);
            _n = 0;
        }
    }

    private static readonly byte[] Order =
    {
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    private static readonly byte[] Zig =
    {
        0, 1, 5, 6, 14, 15, 27, 28,
        2, 4, 7, 13, 16, 26, 29, 42,
        3, 8, 12, 17, 25, 30, 41, 43,
        9, 11, 18, 24, 31, 40, 44, 53,
        10, 19, 23, 32, 39, 45, 52, 54,
        20, 22, 33, 38, 46, 51, 55, 60,
        21, 34, 37, 47, 50, 56, 59, 61,
        35, 36, 48, 49, 57, 58, 62, 63
    };

    private static readonly byte[] LumaQ =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99
    };

    private static readonly byte[] ChromaQ =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99
    };

    private static readonly byte[] YdcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] YdcVal = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] CdcBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] CdcVal = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] YacBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };
    private static readonly byte[] CacBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] YacVal =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    };
    private static readonly byte[] CacVal =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA
    };
}
