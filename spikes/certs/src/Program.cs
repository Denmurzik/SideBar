using System.Security.Cryptography;
using System.Text.Json;
using SideBar.Certs;

// Prints structure only: counts, lengths, field numbers, validity booleans. Never key material or identifiers.
string sharing = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "Google", "Nearby", "Sharing");
var now = DateTimeOffset.UtcNow;

Console.WriteLine("== preferences.json");
using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sharing, "preferences.json")));
var root = doc.RootElement;
foreach (var p in root.EnumerateObject())
{
    string kind = p.Value.ValueKind switch
    {
        JsonValueKind.Object => $"object[{p.Value.EnumerateObject().Count()}]",
        JsonValueKind.Array => $"array[{p.Value.GetArrayLength()}]",
        JsonValueKind.String => $"string[len={p.Value.GetString()!.Length}]",
        _ => p.Value.ValueKind.ToString(),
    };
    Console.WriteLine($"  {p.Name}: {kind}");
}

var privs = root.GetProperty("nearby_sharing.private_certificate_list").EnumerateArray()
    .Select(PrivateCertificate.FromJson).ToList();
Console.WriteLine($"private certs: {privs.Count}");
int idx = 0;
foreach (var c in privs)
{
    idx++;
    var id2 = SHA256.HashData(c.SecretKey);
    bool idOk = id2.AsSpan().SequenceEqual(c.Id);
    bool keyOk;
    try { c.PublicKeySpki(); keyOk = true; } catch { keyOk = false; }
    var md = Proto.Parse(c.UnencryptedMetadata);
    bool valid = now >= c.NotBefore && now < c.NotAfter;
    double hoursLeft = (c.NotAfter - now).TotalHours;
    Console.WriteLine($"  #{idx} visibility={c.Visibility} window={(c.NotAfter - c.NotBefore).TotalHours:F0}h validNow={valid} hoursLeft={hoursLeft:F1} " +
        $"keyPairPkcs8={c.KeyPairPkcs8.Length}B secret={c.SecretKey.Length}B metaKey={c.MetadataEncryptionKey.Length}B id={c.Id.Length}B " +
        $"id==sha256(secret)={idOk} pkcs8Imports={keyOk} salts={c.ConsumedSalts.Count} metadataFields=[{Describe(md)}]");
}

// Self round trip: sign / verify, encrypt metadata key / receiver-side decrypt of a public cert built from this private one.
var pc = privs.First(c => now >= c.NotBefore && now < c.NotAfter);
byte[] payload = RandomNumberGenerator.GetBytes(40);
var spki = pc.PublicKeySpki();
var fake = new PublicCertificate { PublicKey = spki };
Console.WriteLine($"sign/verify DER P-256 roundtrip: {fake.VerifySignature(payload, pc.Sign(payload))} (spki {spki.Length}B)");
var (salt, enc) = pc.EncryptMetadataKey([0x12, 0x34]);
var back = NearbyCrypto.Ctr(pc.SecretKey, salt, enc);
Console.WriteLine($"ctr roundtrip metadata key: {back.AsSpan().SequenceEqual(pc.MetadataEncryptionKey)} (enc {enc.Length}B)");

Console.WriteLine("== public certificate database");
string tmp = Path.Combine(Path.GetTempPath(), "sidebar-certs-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    Directory.CreateDirectory(tmp);
    string src = Path.Combine(sharing, "NearbySharePublicCertificateDatabase");
    foreach (var f in Directory.GetFiles(src))
    {
        if (Path.GetFileName(f) == "LOCK") continue;
        using var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var o = File.Create(Path.Combine(tmp, Path.GetFileName(f)));
        i.CopyTo(o);
    }
    var db = LevelDb.Open(tmp);
    Console.WriteLine($"leveldb: tables={db.TableCount} logs={db.LogCount} entries={db.Entries.Count} tombstones={db.TombstoneCount} snappyBlocks={db.CompressedBlocks} rawBlocks={db.UncompressedBlocks}");
    Console.WriteLine($"key lengths: {string.Join(",", db.Entries.Keys.Select(k => k.Length).Distinct().Order())}");

    var pubs = db.Entries.Select(kv => (Key: kv.Key, Cert: PublicCertificate.Parse(kv.Value))).ToList();
    int keyEqId = pubs.Count(p => p.Key.Length == p.Cert.SecretId.Length && p.Key == System.Text.Encoding.Latin1.GetString(p.Cert.SecretId));
    long nowSec = now.ToUnixTimeSeconds();
    Console.WriteLine($"key==secret_id: {keyEqId}/{pubs.Count}; structurallyValid: {pubs.Count(p => p.Cert.IsStructurallyValid)}; " +
        $"forSelfShare: {pubs.Count(p => p.Cert.ForSelfShare)}; metadataKeyPresent(field7): {pubs.Count(p => p.Cert.MetadataEncryptionKey.Length > 0)}; " +
        $"currentlyValid: {pubs.Count(p => nowSec >= p.Cert.StartSeconds && nowSec < p.Cert.EndSeconds)}; bindingId: {pubs.Count(p => p.Cert.BindingId.Length > 0)}");

    // Receiver flow using the field-7 key as if it had been recovered from an advertisement.
    int tagOk = 0, mdOk = 0;
    var mdShapes = new Dictionary<string, int>();
    foreach (var (_, c) in pubs)
    {
        if (c.MetadataEncryptionKey.Length == 0) continue;
        var hmac = HMACSHA256.HashData(new byte[32], c.MetadataEncryptionKey);
        if (hmac.AsSpan().SequenceEqual(c.KeyTag)) tagOk++;
        var md = c.DecryptMetadata(c.MetadataEncryptionKey);
        if (md is null) continue;
        mdOk++;
        string s = Describe(Proto.Parse(md));
        mdShapes[s] = mdShapes.GetValueOrDefault(s) + 1;
    }
    var ownIds = privs.Select(p => Convert.ToHexString(p.Id)).ToHashSet();
    var own = pubs.Where(p => ownIds.Contains(Convert.ToHexString(p.Cert.SecretId))).ToList();
    var others = pubs.Except(own).ToList();
    Console.WriteLine($"public certs whose id equals one of our private cert ids: {own.Count}");
    foreach (var (name, grp) in new[] { ("own-device", own), ("other-devices", others) })
    {
        var validNow = grp.Where(p => nowSec >= p.Cert.StartSeconds && nowSec < p.Cert.EndSeconds).ToList();
        double latest = grp.Count == 0 ? 0 : (grp.Max(p => p.Cert.EndSeconds) - nowSec) / 3600.0;
        Console.WriteLine($"  {name}: total={grp.Count} validNow={validNow.Count} hoursUntilLatestEnd={latest:F1}");
    }
    Console.WriteLine($"keyTag==HMAC(0x00*32, field7): {tagOk}; metadata AES-GCM decrypt ok: {mdOk}");
    foreach (var (s, n) in mdShapes) Console.WriteLine($"  decrypted metadata shape x{n}: [{s}]");
    var extra = pubs.Where(p => p.Cert.Raw.Any(f => f.Number == 11)).Select(p => Describe(Proto.Parse(p.Cert.Raw.First(f => f.Number == 11).Bytes!))).Distinct();
    foreach (var s in extra) Console.WriteLine($"  undocumented field 11 shape: [{s}]");

    var expDict = root.GetProperty("nearbyshare.public_certificate_expiration_dict");
    Console.WriteLine($"expiration dict entries: {expDict.EnumerateObject().Count()}; matches db keys: " +
        $"{expDict.EnumerateObject().Count(p => db.Entries.Keys.Any(k => ToB64Url(k) == p.Name))}");
}
finally
{
    try { Directory.Delete(tmp, true); } catch { }
}

static string ToB64Url(string latin1) =>
    Convert.ToBase64String(System.Text.Encoding.Latin1.GetBytes(latin1)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string Describe(List<PbField> f) =>
    string.Join(" ", f.Select(x => x.WireType == 2 ? $"{x.Number}:len{x.Bytes!.Length}" : $"{x.Number}:v"));
