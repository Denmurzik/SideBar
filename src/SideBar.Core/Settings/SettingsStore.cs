using System.Text.Json;
using SideBar.Core.Nearby;

namespace SideBar.Core.Settings;

/// <summary>
/// settings.json. Загрузка в конструкторе: нет файла — значения по умолчанию; нечитаемый (битый JSON, пустой, <c>null</c>) — уезжает в
/// <c>settings.bad-yyyyMMdd-HHmmss.json</c>, берутся значения по умолчанию. Файл, занятый и после повторов, не считается битым: берутся
/// значения по умолчанию, файл не трогается, запись подавляется, пока он не прочитается (проверка при следующем Update).
/// Запись атомарна (<c>.tmp</c> + <c>File.Move(overwrite)</c>). Потокобезопасен; <see cref="Changed"/> приходит вне блокировки и по порядку,
/// в том числе при вложенном Update из обработчика.
/// </summary>
public sealed class SettingsStore
{
    const int MaxPcNameLength = 32, LoadRetries = 4, LoadRetryDelayMs = 200, SaveRetries = 3, SaveRetryDelayMs = 100;

    readonly string _path;
    readonly object _gate = new();
    readonly Queue<AppSettings> _pending = new();
    bool _draining;
    bool _writesSuppressed;
    volatile AppSettings _current;
    volatile bool _lastSaveFailed;
    Visibility _persistedVisibility;
    readonly bool[] _dirty = new bool[Fields.Length];   // поля, изменённые, пока файл не читался: после чтения накладываются поверх файла

    // Копирование поля без рефлексии (AOT): геттер для сравнения, перенос значения из одного набора в другой.
    sealed record Field(Func<AppSettings, object?> Get, Func<AppSettings, AppSettings, AppSettings> Copy);

    static readonly Field[] Fields =
    [
        new(s => s.Language, (f, t) => t with { Language = f.Language }),
        new(s => s.CheckForUpdates, (f, t) => t with { CheckForUpdates = f.CheckForUpdates }),
        new(s => s.Visibility, (f, t) => t with { Visibility = f.Visibility }),
        new(s => s.PcName, (f, t) => t with { PcName = f.PcName }),
        new(s => s.ReceiveFolder, (f, t) => t with { ReceiveFolder = f.ReceiveFolder }),
        new(s => s.OfficialExePath, (f, t) => t with { OfficialExePath = f.OfficialExePath }),
        new(s => s.EdgeOpenDelay, (f, t) => t with { EdgeOpenDelay = f.EdgeOpenDelay }),
        new(s => s.BounceOnIncoming, (f, t) => t with { BounceOnIncoming = f.BounceOnIncoming }),
        new(s => s.ShowInFullscreen, (f, t) => t with { ShowInFullscreen = f.ShowInFullscreen }),
        new(s => s.ShelfRetention, (f, t) => t with { ShelfRetention = f.ShelfRetention }),
    ];

    public SettingsStore(string path)
    {
        _path = path;
        if (!TryLoad(LoadRetries, out var loaded))
        {
            loaded = new AppSettings();
            _writesSuppressed = true;
            _lastSaveFailed = true;
        }
        _current = loaded;
        _persistedVisibility = loaded.Visibility;
    }

    public AppSettings Current => _current;

    /// <summary>Видимость, которая пишется в файл: «Все» (временный режим) не сохраняется, пишется последний постоянный режим.</summary>
    public Visibility PersistedVisibility => _persistedVisibility;

    /// <summary>Последняя запись не удалась (или файл не удалось прочитать и запись подавлена): настройки действуют до перезапуска.</summary>
    public bool LastSaveFailed => _lastSaveFailed;

    public event Action<AppSettings>? Changed;

    public event Action? SaveFailed;

    /// <summary>
    /// Применяет изменение, нормализует результат и сохраняет. <see cref="Changed"/> — по одному разу на реальное изменение.
    /// <paramref name="change"/> вернул null или то же значение — ничего не происходит. Сбой записи не бросает: <see cref="Current"/> обновлён,
    /// <see cref="Changed"/> поднят, затем <see cref="SaveFailed"/>.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings?> change)
    {
        var saveFailed = false;
        lock (_gate)
        {
            var old = _current;
            if (_writesSuppressed && TryLoad(0, out var reloaded))
            {
                _writesSuppressed = false;
                // правки, сделанные пока файл был занят, не теряются: поля, которые менялись в памяти, ложатся поверх прочитанного файла
                var merged = reloaded;
                for (var k = 0; k < Fields.Length; k++)
                    if (_dirty[k]) merged = Fields[k].Copy(_current, merged);
                Array.Clear(_dirty);
                _current = Normalize(merged);
                _persistedVisibility = _current.Visibility == Visibility.Everyone ? reloaded.Visibility : _current.Visibility;
            }
            var next = Normalize(change(_current) ?? _current);
            if (next == old) return;
            if (_writesSuppressed)
                for (var k = 0; k < Fields.Length; k++)
                    if (!Equals(Fields[k].Get(old), Fields[k].Get(next))) _dirty[k] = true;
            _current = next;
            if (next.Visibility != Visibility.Everyone) _persistedVisibility = next.Visibility;
            if (_writesSuppressed) saveFailed = true;
            else saveFailed = !TrySave(next);
            _lastSaveFailed = saveFailed;
            _pending.Enqueue(next);
        }
        Drain();
        if (saveFailed) SaveFailed?.Invoke();
    }

    void Drain()
    {
        lock (_gate)
        {
            if (_draining) return;   // вложенный/параллельный Update: событие доставит тот, кто уже разгребает очередь, по порядку
            _draining = true;
        }
        try
        {
            while (true)
            {
                AppSettings value;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out value!)) { _draining = false; return; }
                }
                Changed?.Invoke(value);
            }
        }
        catch
        {
            lock (_gate) _draining = false;
            throw;
        }
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

    /// <summary>
    /// Перенос видимости, сохранённой M4 в реестре: только пока в настройках стоит значение по умолчанию (OwnDevices); «Все» не переносится
    /// (временный режим не сохраняется). Чистая функция: App сам читает реестр и удаляет значение сразу после этого вызова.
    /// </summary>
    public static AppSettings MigrateVisibility(AppSettings settings, Visibility? fromRegistry) =>
        fromRegistry is { } v && v != Visibility.Everyone && Enum.IsDefined(v) && settings.Visibility == new AppSettings().Visibility
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

    static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } x ? x.GetString() : null;

    static bool? Bool(JsonElement? e) => e?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };

    static T ParseEnum<T>(JsonElement? e, T fallback) where T : struct, Enum =>
        Str(e) is { Length: > 0 } raw && !char.IsDigit(raw[0]) && Enum.TryParse<T>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : fallback;

    static AppSettings FromFile(SettingsRaw f)
    {
        var d = new AppSettings();
        var visibility = ParseEnum(f.Visibility, d.Visibility);
        return Normalize(new AppSettings
        {
            Language = ParseEnum(f.Language, d.Language),
            CheckForUpdates = Bool(f.CheckForUpdates) ?? d.CheckForUpdates,
            Visibility = visibility == Visibility.Everyone ? d.Visibility : visibility,   // «Все» — временный режим, после перезапуска не живёт
            PcName = Str(f.PcName),
            ReceiveFolder = Str(f.ReceiveFolder),
            OfficialExePath = Str(f.OfficialExePath),
            EdgeOpenDelay = ParseEnum(f.EdgeOpenDelay, d.EdgeOpenDelay),
            BounceOnIncoming = Bool(f.BounceOnIncoming) ?? d.BounceOnIncoming,
            ShowInFullscreen = Bool(f.ShowInFullscreen) ?? d.ShowInFullscreen,
            ShelfRetention = ParseEnum(f.ShelfRetention, d.ShelfRetention),
        });
    }

    SettingsFile ToFile(AppSettings s) => new()
    {
        Language = s.Language.ToString(),
        CheckForUpdates = s.CheckForUpdates,
        Visibility = (s.Visibility == Visibility.Everyone ? _persistedVisibility : s.Visibility).ToString(),
        PcName = s.PcName,
        ReceiveFolder = s.ReceiveFolder,
        OfficialExePath = s.OfficialExePath,
        EdgeOpenDelay = s.EdgeOpenDelay.ToString(),
        BounceOnIncoming = s.BounceOnIncoming,
        ShowInFullscreen = s.ShowInFullscreen,
        ShelfRetention = s.ShelfRetention.ToString(),
    };

    /// <summary>true — настройки получены (из файла, либо файла нет, либо он битый и убран в карантин: тогда значения по умолчанию). false — файл занят/недоступен.</summary>
    bool TryLoad(int retries, out AppSettings settings)
    {
        settings = new AppSettings();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return true;
                var bytes = File.ReadAllBytes(_path);
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) bytes = bytes[3..];
                var file = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.SettingsRaw);
                if (file is null) { Quarantine(); return true; }
                settings = FromFile(file);
                return true;
            }
            catch (JsonException)
            {
                Quarantine();
                return true;
            }
            catch (IOException e) when (attempt < retries && e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(LoadRetryDelayMs);   // антивирус/индексатор держит файл коротко
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e is FileNotFoundException or DirectoryNotFoundException;   // исчез между Exists и чтением — как «нет файла»
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

    bool TrySave(AppSettings s)
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
                    return true;
                }
                catch (IOException) when (attempt < SaveRetries) { Thread.Sleep(SaveRetryDelayMs); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
