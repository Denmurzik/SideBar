using System.Text.Json;
using SideBar.Core.Nearby;

namespace SideBar.Core.Settings;

/// <summary>
/// settings.json. Загрузка в конструкторе: нет файла — значения по умолчанию; нечитаемый — уезжает в <c>settings.bad-yyyyMMdd-HHmmss.json</c>,
/// берутся значения по умолчанию. Запись атомарна (<c>.tmp</c> + <c>File.Move(overwrite)</c>). Не потокобезопасен (UI-поток).
/// </summary>
public sealed class SettingsStore
{
    const int MaxPcNameLength = 32, LoadRetries = 4, LoadRetryDelayMs = 200, SaveRetries = 3, SaveRetryDelayMs = 100;

    readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    /// <summary>
    /// Применяет изменение, нормализует результат и сохраняет. <see cref="Changed"/> — один раз, если значение изменилось.
    /// Сбой записи не бросает: настройка действует до перезапуска.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = Normalize(change(Current));
        if (next == Current) return;
        Current = next;
        TrySave(next);
        Changed?.Invoke(next);
    }

    /// <summary>Имя ПК: как имена устройств (без управляющих символов), до 32 символов без разрыва суррогатной пары; пусто — null (имя компьютера).</summary>
    public static string? SanitizePcName(string? name)
    {
        var clean = DisplayText.SanitizeName(name, "");
        if (clean.Length > MaxPcNameLength)
        {
            var cut = MaxPcNameLength;
            if (char.IsLowSurrogate(clean[cut]) && char.IsHighSurrogate(clean[cut - 1])) cut--;
            clean = clean[..cut].TrimEnd();
        }
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Перенос видимости, сохранённой M4 в реестре: только пока в настройках стоит значение по умолчанию (Hidden). Чистая функция.</summary>
    public static AppSettings MigrateVisibility(AppSettings settings, Visibility? fromRegistry) =>
        fromRegistry is { } v && Enum.IsDefined(v) && settings.Visibility == Visibility.Hidden
            ? settings with { Visibility = v }
            : settings;

    static string? CleanPath(string? p) => string.IsNullOrWhiteSpace(p) ? null : p.Trim();

    static AppSettings Normalize(AppSettings s)
    {
        var d = new AppSettings();
        return s with
        {
            Language = Enum.IsDefined(s.Language) ? s.Language : d.Language,
            Visibility = Enum.IsDefined(s.Visibility) ? s.Visibility : d.Visibility,
            EdgeOpenDelay = Enum.IsDefined(s.EdgeOpenDelay) ? s.EdgeOpenDelay : d.EdgeOpenDelay,
            ShelfRetention = Enum.IsDefined(s.ShelfRetention) ? s.ShelfRetention : d.ShelfRetention,
            PcName = SanitizePcName(s.PcName),
            ReceiveFolder = CleanPath(s.ReceiveFolder),
            OfficialExePath = CleanPath(s.OfficialExePath),
        };
    }

    static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum =>
        raw is { Length: > 0 } && !char.IsDigit(raw[0]) && Enum.TryParse<T>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : fallback;

    static AppSettings FromFile(SettingsFile f)
    {
        var d = new AppSettings();
        return Normalize(new AppSettings
        {
            Language = ParseEnum(f.Language, d.Language),
            CheckForUpdates = f.CheckForUpdates ?? d.CheckForUpdates,
            Visibility = ParseEnum(f.Visibility, d.Visibility),
            PcName = f.PcName,
            ReceiveFolder = f.ReceiveFolder,
            OfficialExePath = f.OfficialExePath,
            EdgeOpenDelay = ParseEnum(f.EdgeOpenDelay, d.EdgeOpenDelay),
            BounceOnIncoming = f.BounceOnIncoming ?? d.BounceOnIncoming,
            ShowInFullscreen = f.ShowInFullscreen ?? d.ShowInFullscreen,
            ShelfRetention = ParseEnum(f.ShelfRetention, d.ShelfRetention),
        });
    }

    static SettingsFile ToFile(AppSettings s) => new()
    {
        Language = s.Language.ToString(),
        CheckForUpdates = s.CheckForUpdates,
        Visibility = s.Visibility.ToString(),
        PcName = s.PcName,
        ReceiveFolder = s.ReceiveFolder,
        OfficialExePath = s.OfficialExePath,
        EdgeOpenDelay = s.EdgeOpenDelay.ToString(),
        BounceOnIncoming = s.BounceOnIncoming,
        ShowInFullscreen = s.ShowInFullscreen,
        ShelfRetention = s.ShelfRetention.ToString(),
    };

    AppSettings Load()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings();
                var bytes = File.ReadAllBytes(_path);
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) bytes = bytes[3..];
                var file = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.SettingsFile);
                if (file is null) { Quarantine(); return new AppSettings(); }
                return FromFile(file);
            }
            catch (IOException e) when (attempt < LoadRetries && e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(LoadRetryDelayMs);   // антивирус/индексатор держит файл коротко: не принимаем его за битый
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Quarantine();
                return new AppSettings();
            }
        }
    }

    void Quarantine()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dir = Path.GetDirectoryName(_path) ?? ".";
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bad = Path.Combine(dir, $"settings.bad-{stamp}.json");
            for (var n = 2; File.Exists(bad); n++) bad = Path.Combine(dir, $"settings.bad-{stamp}-{n}.json");
            try { File.Move(_path, bad, overwrite: false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { File.Copy(_path, bad, overwrite: false); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void TrySave(AppSettings s)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, ToFile(s), SettingsJsonContext.Default.SettingsFile);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(tmp, _path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < SaveRetries) { Thread.Sleep(SaveRetryDelayMs); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
