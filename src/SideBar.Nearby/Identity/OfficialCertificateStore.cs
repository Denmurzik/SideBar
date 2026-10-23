using System.Text.Json;
using SideBar.Nearby.Certs;

namespace SideBar.Nearby.Identity;

/// <summary>Why the official app's certificates could not be used. Never carries file content.</summary>
public enum CertLoadFailure
{
    /// <summary>The Quick Share data is not there (app not installed or never signed in).</summary>
    Missing,
    /// <summary>The files exist but could not be read (locked, access denied), even after retries.</summary>
    Unreadable,
    /// <summary>The files were read but do not look like what we know (corrupt, or Google changed the format).</summary>
    FormatChanged,
    /// <summary>Readable, but no SELF_SHARE private certificate is valid right now.</summary>
    NoValidCertificate,
}

/// <summary>Non-secret facts about the loaded certificates; safe to print (device names are allowed).</summary>
public sealed record CertificateSummary(
    string SelectedVisibility,
    int PrivateCount,
    int PublicCount,
    int PeerCount,
    DateTimeOffset NextExpiry,
    IReadOnlyList<string> PeerNames);

/// <summary>
/// Reads the official Quick Share app's certificates (read-only, in memory only) and exposes them as a
/// <see cref="CertificateIdentity"/>. <see cref="FilesChanged"/> fires (debounced) when the files change and when the
/// selected private certificate expires, so the caller reloads and re-registers without restarting.
/// </summary>
/// <param name="sharingDir">Directory holding preferences.json and the NearbySharePublicCertificateDatabase leveldb.</param>
/// <param name="now">Clock used to pick valid certificates and compute expiry.</param>
/// <param name="timers">Source of the debounce and expiry timers (tests inject a manual one).</param>
/// <param name="sleep">Pause between load retries (tests inject a no-op or a hook).</param>
public sealed class OfficialCertificateStore(
    string sharingDir, Func<DateTimeOffset> now, TimeProvider? timers = null, Action<TimeSpan>? sleep = null)
    : IDisposable
{
    public const string PreferencesFile = "preferences.json";
    public const string PublicDatabaseDirectory = "NearbySharePublicCertificateDatabase";

    /// <summary>nearby_sharing.private_certificate_list[].visibility value of SELF_SHARE.</summary>
    private const int SelfShareVisibility = 2;
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    // google/nearby accepts public certificates 30 minutes outside their window.
    private static readonly TimeSpan PublicTolerance = TimeSpan.FromMinutes(30);
    // Timer due times are limited (about 49 days); a longer wait just re-arms through an earlier reload.
    private static readonly TimeSpan MaxTimerWait = TimeSpan.FromDays(20);

    private readonly TimeProvider _timers = timers ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _debounce;
    private ITimer? _expiry;
    private bool _started;
    private bool _disposed;

    public static string DefaultSharingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby", "Sharing");

    /// <summary>Earliest not_after of the valid SELF_SHARE private certificates of the last successful load.</summary>
    public DateTimeOffset? NextExpiry { get; private set; }

    /// <summary>Summary of the last successful load (null before one).</summary>
    public CertificateSummary? Summary { get; private set; }

    /// <summary>Raised on a thread-pool thread: files changed (after 2 s of quiet) or the selected certificate expired.</summary>
    public event Action? FilesChanged;

    /// <summary>
    /// Loads the certificates. Never throws: every failure becomes a <see cref="CertLoadFailure"/>. A locked or
    /// half-written file (the official app compacting its database) is retried up to 3 times, 200 ms apart, which
    /// blocks the caller for up to 400 ms in the worst case.
    /// </summary>
    public bool TryLoad(out CertificateIdentity? identity, out CertLoadFailure? failure)
    {
        identity = null;
        failure = CertLoadFailure.Unreadable;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var result = LoadOnce();
            if (result.Identity is not null)
            {
                identity = result.Identity;
                failure = null;
                lock (_gate)
                {
                    NextExpiry = result.Summary!.NextExpiry;
                    Summary = result.Summary;
                    ArmExpiry();
                }
                return true;
            }

            failure = result.Failure;
            if (failure is CertLoadFailure.Missing or CertLoadFailure.NoValidCertificate) break;
            if (attempt < Attempts) (sleep ?? Thread.Sleep)(RetryDelay);
        }
        lock (_gate)
        {
            NextExpiry = null;
            Summary = null;
            _expiry?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        return false;
    }

    /// <summary>Starts watching preferences.json and the leveldb directory, and arms the expiry timer.</summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _debounce = _timers.CreateTimer(_ => Raise(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _expiry = _timers.CreateTimer(_ => Raise(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ArmExpiry();
            try
            {
                var watcher = new FileSystemWatcher(sharingDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += (s, e) => OnFileEvent(s, e);
                watcher.Error += (_, _) => NotifyFilesChanged();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Directory missing: nothing to watch; the caller's own retry or the expiry timer covers it.
            }
        }
    }

    /// <summary>Restarts the 2 s debounce; the event fires once after the files stay quiet. Also used by the watcher.</summary>
    public void NotifyFilesChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _debounce?.Dispose();
            _expiry?.Dispose();
            _watcher = null;
            _debounce = null;
            _expiry = null;
        }
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        // The leveldb LOCK file is touched by the official app all the time and carries no data.
        if (name.EndsWith("LOCK", StringComparison.Ordinal)) return;
        if (name == PreferencesFile || name.StartsWith(PublicDatabaseDirectory, StringComparison.Ordinal))
            NotifyFilesChanged();
    }

    private void Raise() => FilesChanged?.Invoke();

    // Caller holds _gate.
    private void ArmExpiry()
    {
        if (_expiry is null || NextExpiry is not { } expiry) return;
        var wait = expiry - now();
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > MaxTimerWait) wait = MaxTimerWait;
        _expiry.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private readonly record struct LoadResult(CertificateIdentity? Identity, CertificateSummary? Summary, CertLoadFailure Failure);

    private LoadResult LoadOnce()
    {
        var preferences = Path.Combine(sharingDir, PreferencesFile);
        var database = Path.Combine(sharingDir, PublicDatabaseDirectory);
        try
        {
            if (!File.Exists(preferences) || !Directory.Exists(database))
                return new(null, null, CertLoadFailure.Missing);

            var at = now();
            var privates = ParsePrivates(ReadShared(preferences));
            var publics = LevelDbReader.Open(ReadDirectory(database)).Entries.Values.Select(PublicCertificate.Parse).ToList();
            return Build(privates, publics, at);
        }
        catch (Exception e)
        {
            // Only the category is kept: exception messages could echo file content.
            return new(null, null, e is IOException or UnauthorizedAccessException
                ? CertLoadFailure.Unreadable
                : CertLoadFailure.FormatChanged);
        }
    }

    private static List<PrivateCertificate> ParsePrivates(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("nearby_sharing.private_certificate_list").EnumerateArray()
            .Select(PrivateCertificate.FromJson).ToList();
    }

    private static LoadResult Build(
        IReadOnlyList<PrivateCertificate> privates, IReadOnlyList<PublicCertificate> publics, DateTimeOffset at)
    {
        var validSelf = privates
            .Where(c => c.Visibility == SelfShareVisibility && at >= c.NotBefore && at < c.NotAfter).ToList();
        if (validSelf.Count == 0) return new(null, null, CertLoadFailure.NoValidCertificate);
        var self = validSelf[0];
        var nextExpiry = validSelf.Min(c => c.NotAfter);

        var ownIds = privates.Select(c => Convert.ToHexString(c.Id)).ToHashSet();
        var nowSeconds = at.ToUnixTimeSeconds();
        var tolerance = (long)PublicTolerance.TotalSeconds;
        var peers = publics
            .Where(c => c.IsStructurallyValid
                && nowSeconds >= c.StartSeconds - tolerance && nowSeconds < c.EndSeconds + tolerance)
            .Where(c => !ownIds.Contains(Convert.ToHexString(c.SecretId)))
            .GroupBy(c => Convert.ToHexString(c.SecretId)).Select(g => g.First()).ToList();

        var names = peers.Select(PeerName).Where(n => n is not null).Select(n => n!).Distinct().ToList();
        var salts = self.ConsumedSalts.Select(s => (ushort)((s[0] << 8) | s[1])).ToHashSet();
        var identity = new CertificateIdentity(
            new PrivateCertificateData(self.SecretKey, self.MetadataEncryptionKey, self.KeyPairPkcs8, salts),
            peers.Select(c => new PublicCertificateData(c.SecretId, c.SecretKey, c.PublicKey, c.KeyTag, c.EncryptedMetadata))
                .ToList());
        var summary = new CertificateSummary(
            "SELF_SHARE", privates.Count, publics.Count, peers.Count, nextExpiry, names);
        return new(identity, summary, default);
    }

    // Self-share certificates carry the metadata key in field 7, which lets us name a peer without an advertisement.
    private static string? PeerName(PublicCertificate cert) =>
        cert.MetadataEncryptionKey.Length == 0
            ? null
            : CertificateIdentity.DeviceName(
                CertificateIdentity.DecryptMetadata(cert.SecretKey, cert.MetadataEncryptionKey, cert.EncryptedMetadata));

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // The whole leveldb directory in memory (file name -> bytes); nothing is copied to disk.
    private static Dictionary<string, byte[]> ReadDirectory(string directory)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (name != "CURRENT" && !name.StartsWith("MANIFEST-", StringComparison.Ordinal)
                && !name.EndsWith(".ldb", StringComparison.Ordinal) && !name.EndsWith(".sst", StringComparison.Ordinal)
                && !name.EndsWith(".log", StringComparison.Ordinal))
                continue;
            files[name] = ReadShared(path);
        }
        return files;
    }
}
