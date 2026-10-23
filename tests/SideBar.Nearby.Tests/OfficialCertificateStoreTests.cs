using System.Security.Cryptography;
using SideBar.Nearby.Identity;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using static SideBar.Nearby.Tests.CertFixtures;

namespace SideBar.Nearby.Tests;

/// <summary>Synthetic certificates only; nothing here touches the real official-app data.</summary>
public class OfficialCertificateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidebar-store-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = Now;
    private readonly ManualTimeProvider _timers = new();

    private string Dir => Path.Combine(_root, "Sharing");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private OfficialCertificateStore NewStore(Action<TimeSpan>? sleep = null) =>
        new(Dir, () => _now, _timers, sleep ?? (_ => { }));

    private CertificateIdentity Load(Device self, params Device[] peers)
    {
        WriteSharingDir(Dir, [self.Private], [self.Public, .. peers.Select(p => p.Public)]);
        using var store = NewStore();
        Assert.True(store.TryLoad(out var identity, out var failure), failure?.ToString());
        return identity!;
    }

    [Fact]
    public void TryLoad_selects_self_share_and_counts_peers()
    {
        var pc = NewDevice("Den PC");
        var phone = NewDevice("Pixel 9");
        WriteSharingDir(Dir, [pc.Private, NewDevice("x", visibility: 3).Private], [pc.Public, phone.Public]);
        using var store = NewStore();

        Assert.True(store.TryLoad(out var identity, out var failure));

        Assert.Null(failure);
        var s = store.Summary!;
        Assert.Equal("SELF_SHARE", s.SelectedVisibility);
        Assert.Equal(2, s.PrivateCount);
        Assert.Equal(2, s.PublicCount);
        Assert.Equal(1, s.PeerCount);
        Assert.Equal(["Pixel 9"], s.PeerNames);
        Assert.True(identity!.SelfTestSignature());
    }

    [Fact]
    public void TryLoad_ignores_expired_public_certificates()
    {
        var pc = NewDevice("PC");
        var old = NewDevice("Old phone", offset: TimeSpan.FromDays(-10));
        WriteSharingDir(Dir, [pc.Private], [pc.Public, old.Public]);
        using var store = NewStore();

        Assert.True(store.TryLoad(out _, out _));

        Assert.Equal(0, store.Summary!.PeerCount);
        Assert.Empty(store.Summary.PeerNames);
    }

    [Fact]
    public void TryLoad_no_valid_cert_reports_NoValidCertificate()
    {
        var expired = NewDevice("PC", offset: TimeSpan.FromDays(-10));
        var contacts = NewDevice("PC", visibility: 3);
        WriteSharingDir(Dir, [expired.Private, contacts.Private], []);
        var sleeps = 0;
        using var store = NewStore(_ => sleeps++);

        Assert.False(store.TryLoad(out var identity, out var failure));

        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.NoValidCertificate, failure);
        Assert.Equal(0, sleeps); // not a race: no retries
        Assert.Null(store.NextExpiry);
    }

    [Fact]
    public void TryLoad_missing_directory_is_Missing()
    {
        using var store = NewStore();

        Assert.False(store.TryLoad(out var identity, out var failure));

        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.Missing, failure);
    }

    [Fact]
    public void TryLoad_missing_leveldb_is_Missing()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "preferences.json"), PreferencesJson([NewDevice("PC").Private]));
        using var store = NewStore();

        Assert.False(store.TryLoad(out _, out var failure));
        Assert.Equal(CertLoadFailure.Missing, failure);
    }

    [Fact]
    public void TryLoad_garbage_preferences_is_FormatChanged()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        File.WriteAllText(Path.Combine(Dir, "preferences.json"), "{ this is not json");
        var sleeps = 0;
        using var store = NewStore(_ => sleeps++);

        Assert.False(store.TryLoad(out var identity, out var failure));

        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.FormatChanged, failure);
        Assert.Equal(2, sleeps); // 3 attempts, 2 pauses
    }

    [Fact]
    public void TryLoad_unexpected_preferences_shape_is_FormatChanged()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        File.WriteAllText(Path.Combine(Dir, "preferences.json"), """{"nearby_sharing":{"other":1}}""");
        using var store = NewStore();

        Assert.False(store.TryLoad(out _, out var failure));
        Assert.Equal(CertLoadFailure.FormatChanged, failure);
    }

    [Fact]
    public void TryLoad_garbage_leveldb_is_FormatChanged()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        var db = Path.Combine(Dir, "NearbySharePublicCertificateDatabase");
        File.WriteAllBytes(Path.Combine(db, "000003.log"), Enumerable.Repeat((byte)0xFF, 300).ToArray());
        File.WriteAllBytes(Path.Combine(db, "MANIFEST-000001"), Enumerable.Repeat((byte)0xFF, 300).ToArray());
        using var store = NewStore();

        Assert.False(store.TryLoad(out var identity, out var failure));

        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.FormatChanged, failure);
    }

    [Fact]
    public void TryLoad_leveldb_manifest_pointing_at_a_vanished_file_is_FormatChanged()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        File.WriteAllText(Path.Combine(Dir, "NearbySharePublicCertificateDatabase", "CURRENT"), "MANIFEST-000009\n");
        using var store = NewStore();

        Assert.False(store.TryLoad(out _, out var failure));
        Assert.Equal(CertLoadFailure.FormatChanged, failure);
    }

    [Fact]
    public void TryLoad_locked_file_retries_then_succeeds()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        var prefs = Path.Combine(Dir, "preferences.json");
        var lockHandle = new FileStream(prefs, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var sleeps = 0;
        using var store = NewStore(_ =>
        {
            sleeps++;
            lockHandle.Dispose(); // the official app lets go between attempts
        });

        try
        {
            Assert.True(store.TryLoad(out var identity, out var failure));
            Assert.NotNull(identity);
            Assert.Null(failure);
            Assert.Equal(1, sleeps);
        }
        finally { lockHandle.Dispose(); }
    }

    [Fact]
    public void TryLoad_file_locked_for_good_is_Unreadable_after_three_attempts()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        using var lockHandle = new FileStream(
            Path.Combine(Dir, "preferences.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var delays = new List<TimeSpan>();
        using var store = NewStore(delays.Add);

        Assert.False(store.TryLoad(out var identity, out var failure));

        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.Unreadable, failure);
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)], delays);
    }

    [Fact]
    public void TryLoad_reads_files_in_place_and_leaves_no_copies()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        var before = Directory.GetFiles(Path.GetTempPath(), "sidebar-certs-*").Length;
        using var store = NewStore();

        Assert.True(store.TryLoad(out _, out _));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "sidebar-certs-*").Length);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "sidebar-certs-*").Length);
    }

    [Fact]
    public void NextExpiry_is_min_not_after_of_selected_certs()
    {
        var soon = NewDevice("PC", lifetime: TimeSpan.FromHours(5));
        var later = NewDevice("PC", lifetime: TimeSpan.FromHours(30));
        var contactsEarlier = NewDevice("PC", visibility: 3, lifetime: TimeSpan.FromHours(2));
        WriteSharingDir(Dir, [later.Private, soon.Private, contactsEarlier.Private], []);
        using var store = NewStore();

        Assert.True(store.TryLoad(out _, out _));

        Assert.Equal(soon.Private.NotAfter, store.NextExpiry);
        Assert.Equal(soon.Private.NotAfter, store.Summary!.NextExpiry);
    }

    [Fact]
    public void Peer_advertisement_is_recognised_and_named()
    {
        var pc = NewDevice("Den PC");
        var phone = NewDevice("Pixel 9");
        var pcSide = Load(pc, phone);
        var phoneSide = Load(phone, pc);

        var info = phoneSide.CreateEndpointInfo("whatever", DeviceKind.Phone);

        Assert.Equal("Pixel 9", pcSide.TryGetPeerName(info));
        Assert.True(pcSide.Identify(info));
        Assert.Null(pcSide.TryGetPeerName(EndpointInfo.CreateAnonymous("Pixel 9", DeviceKind.Phone)));
        Assert.False(pcSide.Identify(EndpointInfo.CreateAnonymous("Pixel 9", DeviceKind.Phone)));
    }

    [Fact]
    public void Paired_key_is_verified_between_two_store_loaded_identities()
    {
        var pc = NewDevice("PC");
        var phone = NewDevice("Phone");
        var pcSide = Load(pc, phone);
        var phoneSide = Load(phone, pc);
        var token = RandomNumberGenerator.GetBytes(32);

        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            phoneSide.VerifyPairedKey(
                pcSide.CreatePairedKeyEncryption(token, PairedKeyRole.Sender, phoneSide.CreateEndpointInfo("p", DeviceKind.Phone)),
                token, PairedKeyRole.Receiver, pcSide.CreateEndpointInfo("c", DeviceKind.Laptop)));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            pcSide.VerifyPairedKey(
                phoneSide.CreatePairedKeyEncryption(token, PairedKeyRole.Receiver, pcSide.CreateEndpointInfo("c", DeviceKind.Laptop)),
                token, PairedKeyRole.Sender, phoneSide.CreateEndpointInfo("p", DeviceKind.Phone)));
    }

    [Fact]
    public void A_stranger_posing_as_a_known_name_is_not_identified()
    {
        var pc = NewDevice("PC");
        var phone = NewDevice("Pixel 9");
        var pcSide = Load(pc, phone);
        var stranger = Load(NewDevice("Pixel 9"), NewDevice("Other"));

        var info = stranger.CreateEndpointInfo("Pixel 9", DeviceKind.Phone);

        Assert.False(pcSide.Identify(info));
        Assert.Null(pcSide.TryGetPeerName(info));
    }

    [Fact]
    public void Consumed_salts_from_preferences_are_not_reused()
    {
        var pc = NewDevice("PC");
        // Everything except salt 0xABCD is consumed.
        pc.Private.ConsumedSalts = Enumerable.Range(0, 65536).Where(i => i != 0xABCD)
            .Select(i => new[] { (byte)(i >> 8), (byte)i }).ToList();

        var info = Load(pc).CreateEndpointInfo("PC", DeviceKind.Laptop);

        Assert.Equal(new byte[] { 0xAB, 0xCD }, info.Metadata[..2]);
    }

    [Fact]
    public async Task Debounce_collapses_a_burst_of_file_changes_into_one_event()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        using var store = NewStore();
        var raised = 0;
        store.FilesChanged += () => Interlocked.Increment(ref raised);
        store.StartWatching();
        var debounce = _timers.Timers[0];

        store.NotifyFilesChanged();
        store.NotifyFilesChanged();
        store.NotifyFilesChanged();

        Assert.Equal(TimeSpan.FromSeconds(2), debounce.Due);
        Assert.Equal(0, raised);
        debounce.Fire();
        Assert.Equal(1, raised);
        await Task.CompletedTask;
    }

    [Fact]
    public void Real_file_change_arms_the_debounce_timer()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        using var store = NewStore();
        store.StartWatching();
        var debounce = _timers.Timers[0];
        Assert.Equal(Timeout.InfiniteTimeSpan, debounce.Due);

        File.AppendAllText(Path.Combine(Dir, "preferences.json"), " ");

        SpinWait.SpinUntil(() => debounce.Due == TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(2), debounce.Due);
    }

    [Fact]
    public void Expiry_timer_targets_the_selected_certificate_and_reload_switches_to_the_next_one()
    {
        // The current self-share certificate lapses in 5 hours; its successor is already valid from then on.
        var current = NewDevice("PC", lifetime: TimeSpan.FromHours(5));
        var next = NewDevice("PC", offset: TimeSpan.FromHours(5), lifetime: TimeSpan.FromHours(72));
        WriteSharingDir(Dir, [current.Private, next.Private], []);
        using var store = NewStore();
        var raised = 0;
        store.FilesChanged += () => Interlocked.Increment(ref raised);
        store.StartWatching();
        var expiry = _timers.Timers[1];

        Assert.True(store.TryLoad(out var first, out _));
        Assert.Equal(current.Private.NotAfter - _now, expiry.Due);
        Assert.Equal(current.Private.NotAfter, store.NextExpiry);

        _now = current.Private.NotAfter; // the clock reaches not_after: the timer fires, SideBar reloads
        expiry.Fire();
        Assert.Equal(1, raised);

        Assert.True(store.TryLoad(out var second, out _));
        Assert.NotSame(first, second);
        Assert.Equal(next.Private.NotAfter, store.NextExpiry);
        Assert.Equal(next.Private.NotAfter - _now, expiry.Due);
    }

    [Fact]
    public void Expired_certificate_without_a_successor_degrades_to_NoValidCertificate()
    {
        var current = NewDevice("PC", lifetime: TimeSpan.FromHours(5));
        WriteSharingDir(Dir, [current.Private], []);
        using var store = NewStore();
        store.StartWatching();
        var expiry = _timers.Timers[1];
        Assert.True(store.TryLoad(out _, out _));

        _now = current.Private.NotAfter;

        Assert.False(store.TryLoad(out var identity, out var failure));
        Assert.Null(identity);
        Assert.Equal(CertLoadFailure.NoValidCertificate, failure);
        Assert.Equal(Timeout.InfiniteTimeSpan, expiry.Due);
    }

    [Fact]
    public void Dispose_stops_the_timers()
    {
        var pc = NewDevice("PC");
        WriteSharingDir(Dir, [pc.Private], [pc.Public]);
        var store = NewStore();
        store.StartWatching();

        store.Dispose();

        Assert.All(_timers.Timers, t => Assert.True(t.Disposed));
        store.NotifyFilesChanged(); // no throw after disposal
    }
}
