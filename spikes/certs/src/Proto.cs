namespace SideBar.Certs;

// Minimal protobuf wire reader (no schema). Enough for the handful of Nearby Share messages.
public readonly record struct PbField(int Number, int WireType, ulong Varint, byte[]? Bytes);

public static class Proto
{
    public static List<PbField> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<PbField>();
        int pos = 0;
        while (pos < b.Length)
        {
            ulong tag = LevelDb.Varint(b, ref pos);
            int num = (int)(tag >> 3), wt = (int)(tag & 7);
            switch (wt)
            {
                case 0: list.Add(new(num, wt, LevelDb.Varint(b, ref pos), null)); break;
                case 1: list.Add(new(num, wt, BitConverter.ToUInt64(b.Slice(pos, 8)), null)); pos += 8; break;
                case 2:
                    int n = (int)LevelDb.Varint(b, ref pos);
                    list.Add(new(num, wt, 0, b.Slice(pos, n).ToArray()));
                    pos += n;
                    break;
                case 5: list.Add(new(num, wt, BitConverter.ToUInt32(b.Slice(pos, 4)), null)); pos += 4; break;
                default: throw new InvalidDataException($"bad wire type {wt}");
            }
        }
        return list;
    }

    public static byte[]? Bytes(List<PbField> f, int num) => f.FirstOrDefault(x => x.Number == num && x.WireType == 2).Bytes;
    public static long Int(List<PbField> f, int num) => (long)f.FirstOrDefault(x => x.Number == num && x.WireType == 0).Varint;
}
