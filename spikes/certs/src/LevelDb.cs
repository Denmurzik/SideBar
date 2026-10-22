namespace SideBar.Certs;

// Minimal read-only LevelDB reader: MANIFEST (live files) + .ldb tables + .log write batches.
// Newest sequence wins, deletions drop the key. Intended for a directory copy; never writes.
public sealed class LevelDb
{
    public Dictionary<string, byte[]> Entries { get; } = new(); // key: latin1 string of raw bytes
    public int TableCount { get; private set; }
    public int LogCount { get; private set; }
    public int TombstoneCount { get; private set; }
    public int CompressedBlocks { get; private set; }
    public int UncompressedBlocks { get; private set; }

    readonly Dictionary<string, ulong> _seq = new();

    public static LevelDb Open(string dir)
    {
        var db = new LevelDb();
        string current = File.ReadAllText(Path.Combine(dir, "CURRENT")).Trim();
        var live = new HashSet<long>();
        long logNumber = 0;
        foreach (var rec in LogRecords(File.ReadAllBytes(Path.Combine(dir, current))))
            ParseVersionEdit(rec, live, ref logNumber);

        foreach (long n in live.Order())
        {
            string p = Path.Combine(dir, $"{n:D6}.ldb");
            if (!File.Exists(p)) p = Path.Combine(dir, $"{n:D6}.sst");
            db.ReadTable(File.ReadAllBytes(p));
            db.TableCount++;
        }
        foreach (string p in Directory.GetFiles(dir, "*.log").Order())
        {
            long n = long.Parse(Path.GetFileNameWithoutExtension(p));
            if (n < logNumber) continue;
            foreach (var rec in LogRecords(File.ReadAllBytes(p))) db.ApplyBatch(rec);
            db.LogCount++;
        }
        return db;
    }

    static void ParseVersionEdit(byte[] rec, HashSet<long> live, ref long logNumber)
    {
        int pos = 0;
        while (pos < rec.Length)
        {
            ulong tag = Varint(rec, ref pos);
            switch (tag)
            {
                case 1: SkipBytes(rec, ref pos); break;
                case 2: logNumber = (long)Varint(rec, ref pos); break;
                case 3: case 4: case 9: Varint(rec, ref pos); break;
                case 5: Varint(rec, ref pos); SkipBytes(rec, ref pos); break;
                case 6: Varint(rec, ref pos); live.Remove((long)Varint(rec, ref pos)); break;
                case 7:
                    Varint(rec, ref pos);
                    live.Add((long)Varint(rec, ref pos));
                    Varint(rec, ref pos);
                    SkipBytes(rec, ref pos);
                    SkipBytes(rec, ref pos);
                    break;
                default: throw new InvalidDataException($"unknown VersionEdit tag {tag}");
            }
        }
    }

    static void SkipBytes(byte[] b, ref int pos) { int n = (int)Varint(b, ref pos); pos += n; }

    public static ulong Varint(ReadOnlySpan<byte> b, ref int pos)
    {
        ulong r = 0;
        int s = 0;
        while (true)
        {
            byte x = b[pos++];
            r |= (ulong)(x & 0x7f) << s;
            if ((x & 0x80) == 0) return r;
            s += 7;
        }
    }

    // Log record types: 1 full, 2 first, 3 middle, 4 last.
    static IEnumerable<byte[]> LogRecords(byte[] data)
    {
        const int Block = 32768;
        var cur = new MemoryStream();
        int pos = 0;
        while (pos < data.Length)
        {
            int left = Block - (pos % Block);
            if (left < 7) { pos += left; continue; }
            if (pos + 7 > data.Length) break;
            int len = data[pos + 4] | (data[pos + 5] << 8);
            int type = data[pos + 6];
            if (type == 0 && len == 0) { pos += left; continue; }
            if (pos + 7 + len > data.Length) break;
            if (type == 1 || type == 2) cur.SetLength(0);
            cur.Write(data, pos + 7, len);
            pos += 7 + len;
            if (type == 1 || type == 4) yield return cur.ToArray();
        }
    }

    void ApplyBatch(byte[] rec)
    {
        ulong seq = BitConverter.ToUInt64(rec, 0);
        int count = BitConverter.ToInt32(rec, 8);
        int pos = 12;
        for (int i = 0; i < count; i++)
        {
            byte type = rec[pos++];
            int kl = (int)Varint(rec, ref pos);
            string key = Latin1(rec.AsSpan(pos, kl));
            pos += kl;
            byte[]? val = null;
            if (type == 1)
            {
                int vl = (int)Varint(rec, ref pos);
                val = rec.AsSpan(pos, vl).ToArray();
                pos += vl;
            }
            Put(key, seq + (ulong)i, val);
        }
    }

    void Put(string key, ulong seq, byte[]? val)
    {
        if (_seq.TryGetValue(key, out var old) && old >= seq) return;
        _seq[key] = seq;
        if (val is null) { Entries.Remove(key); TombstoneCount++; }
        else Entries[key] = val;
    }

    void ReadTable(byte[] file)
    {
        var footer = file.AsSpan(file.Length - 48);
        if (BitConverter.ToUInt64(footer[40..]) != 0xdb4775248b80fb57UL) throw new InvalidDataException("bad table magic");
        int p = 0;
        Varint(footer, ref p);
        Varint(footer, ref p); // metaindex handle
        long idxOff = (long)Varint(footer, ref p), idxSize = (long)Varint(footer, ref p);
        foreach (var (_, handle) in BlockEntries(ReadBlock(file, idxOff, idxSize)))
        {
            int hp = 0;
            long off = (long)Varint(handle, ref hp), size = (long)Varint(handle, ref hp);
            foreach (var (ik, v) in BlockEntries(ReadBlock(file, off, size)))
            {
                ulong tail = BitConverter.ToUInt64(ik, ik.Length - 8);
                string key = Latin1(ik.AsSpan(0, ik.Length - 8));
                Put(key, tail >> 8, (tail & 0xff) == 1 ? v : null);
            }
        }
    }

    byte[] ReadBlock(byte[] file, long off, long size)
    {
        var raw = file.AsSpan((int)off, (int)size);
        byte comp = file[off + size];
        if (comp == 0) { UncompressedBlocks++; return raw.ToArray(); }
        if (comp == 1) { CompressedBlocks++; return Snappy.Decompress(raw); }
        throw new InvalidDataException($"unknown block compression {comp}");
    }

    static IEnumerable<(byte[] Key, byte[] Value)> BlockEntries(byte[] block)
    {
        int restarts = BitConverter.ToInt32(block, block.Length - 4);
        int end = block.Length - 4 - restarts * 4;
        int pos = 0;
        var key = new List<byte>();
        while (pos < end)
        {
            int shared = (int)Varint(block, ref pos), nonShared = (int)Varint(block, ref pos), vl = (int)Varint(block, ref pos);
            key.RemoveRange(shared, key.Count - shared);
            key.AddRange(block.AsSpan(pos, nonShared).ToArray());
            pos += nonShared;
            var val = block.AsSpan(pos, vl).ToArray();
            pos += vl;
            yield return (key.ToArray(), val);
        }
    }

    static string Latin1(ReadOnlySpan<byte> b) => System.Text.Encoding.Latin1.GetString(b);
}
