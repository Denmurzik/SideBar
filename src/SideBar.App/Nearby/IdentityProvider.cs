using SideBar.Nearby.Identity;

namespace SideBar.Sharing;

/// <summary>
/// Сертификаты официального Quick Share для "Свои устройства": текущая <see cref="CertificateIdentity"/> (null — недоступны).
/// Меняются файлы или истёк сертификат — идентичность перечитывается и поднимается <see cref="Changed"/>; сессии в работе
/// держат ту идентичность, с которой начались. В логи идут только счётчики и причина (enum): ни имён, ни ключей.
/// </summary>
sealed class IdentityProvider : IDisposable
{
    readonly OfficialCertificateStore _store;
    volatile CertificateIdentity? _current;

    /// <summary>Поднимается на потоке пула после перезагрузки (успешной или нет). Подписчик читает <see cref="Current"/>.</summary>
    public event Action? Changed;

    public CertificateIdentity? Current => _current;

    public IdentityProvider()
    {
        _store = new OfficialCertificateStore(SharingDirectory(), () => DateTimeOffset.UtcNow);
    }

    static string SharingDirectory()
    {
#if DEBUG
        // автотест с синтетическими сертификатами; в Release не компилируется
        if (Environment.GetEnvironmentVariable("SIDEBAR_CERTS_DIR") is { Length: > 0 } dir && Path.IsPathRooted(dir)) return dir;
#endif
        return OfficialCertificateStore.DefaultSharingDirectory;
    }

    /// <summary>Первая загрузка (до 0,4 с при гонке с официальным приложением) и наблюдение за файлами.</summary>
    public void Start()
    {
        Load();
        _store.FilesChanged += OnFilesChanged;
        _store.StartWatching();
    }

    void OnFilesChanged()
    {
        Load();
        Changed?.Invoke();
    }

    void Load()
    {
        try
        {
            if (_store.TryLoad(out var identity, out var failure))
            {
                _current = identity;
                Log.Write($"certs loaded peers={_store.Summary?.PeerCount}");
            }
            else
            {
                _current = null;
                Log.Write($"certs unavailable {failure}");
            }
        }
        catch (Exception e)
        {
            _current = null;
            Log.Write("certs load failed: " + Log.Describe(e));
        }
    }

    public void Dispose() => _store.Dispose();
}
