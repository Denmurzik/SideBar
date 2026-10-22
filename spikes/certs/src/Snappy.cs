namespace SideBar.Certs;

// Minimal raw Snappy block decompressor (as used by LevelDB blocks).
static class Snappy
{
    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        int pos = 0;
        uint len = 0;
        int shift = 0;
        while (true)
        {
            byte b = src[pos++];
            len |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        var dst = new byte[len];
        int d = 0;
        while (pos < src.Length)
        {
            int tag = src[pos++];
            int type = tag & 3;
            if (type == 0)
            {
                int n = tag >> 2;
                if (n >= 60)
                {
                    int extra = n - 59;
                    n = 0;
                    for (int i = 0; i < extra; i++) n |= src[pos++] << (8 * i);
                }
                n++;
                src.Slice(pos, n).CopyTo(dst.AsSpan(d));
                pos += n;
                d += n;
            }
            else
            {
                int n, off;
                if (type == 1)
                {
                    n = ((tag >> 2) & 7) + 4;
                    off = ((tag >> 5) << 8) | src[pos++];
                }
                else if (type == 2)
                {
                    n = (tag >> 2) + 1;
                    off = src[pos] | (src[pos + 1] << 8);
                    pos += 2;
                }
                else
                {
                    n = (tag >> 2) + 1;
                    off = src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24);
                    pos += 4;
                }
                if (off <= 0 || off > d) throw new InvalidDataException("bad snappy offset");
                for (int i = 0; i < n; i++) { dst[d] = dst[d - off]; d++; }
            }
        }
        if (d != dst.Length) throw new InvalidDataException("snappy length mismatch");
        return dst;
    }
}
