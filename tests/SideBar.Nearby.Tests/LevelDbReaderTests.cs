using SideBar.Nearby.Certs;

namespace SideBar.Nearby.Tests;

public class LevelDbReaderTests
{
    [Fact]
    public void Snappy_decodes_literals_and_back_references()
    {
        // length 11, literal "abcd" (tag 3<<2), copy len 4+3=7 offset 4 (type 1: len-4 in bits 2-4, offset 4)
        byte[] compressed = [11, 3 << 2, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (3 << 2) | 1, 4];

        Assert.Equal("abcdabcdabc"u8.ToArray(), Snappy.Decompress(compressed));
    }

    [Fact]
    public void Snappy_rejects_absurd_lengths_and_bad_offsets()
    {
        Assert.Throws<InvalidDataException>(() => Snappy.Decompress([0xFF, 0xFF, 0xFF, 0xFF, 0x0F]));
        Assert.Throws<InvalidDataException>(() => Snappy.Decompress([4, (1 << 2) | 1, 9]));
    }

    [Fact]
    public void Open_reads_log_batches_from_memory()
    {
        var device = CertFixtures.NewDevice("PC");
        var dir = Path.Combine(Path.GetTempPath(), "sidebar-ldb-" + Guid.NewGuid().ToString("N"));
        try
        {
            CertFixtures.WriteLevelDb(dir, [device.Public]);
            var files = Directory.GetFiles(dir).ToDictionary(p => Path.GetFileName(p), p => File.ReadAllBytes(p));

            var db = LevelDbReader.Open(files);

            var entry = Assert.Single(db.Entries);
            Assert.Equal(device.Public.SecretKey, PublicCertificate.Parse(entry.Value).SecretKey);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Open_without_CURRENT_is_InvalidData()
    {
        Assert.Throws<InvalidDataException>(() => LevelDbReader.Open(new Dictionary<string, byte[]>()));
    }
}
