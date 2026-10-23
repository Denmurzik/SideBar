using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Official;

/// <summary>Состояние SideBar в <c>%LocalAppData%\SideBar\state.json</c>. Новые поля добавляются свойствами; неизвестные и отсутствующие поля загрузку не ломают.</summary>
public sealed record AppState
{
    /// <summary>Время последнего успешного цикла обновления сертификатов официальным приложением.</summary>
    [JsonPropertyName("lastSuccess")] public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>Когда показано предупреждение «не удаётся обновить сертификаты» (раз за период без успеха).</summary>
    [JsonPropertyName("lastWarned")] public DateTimeOffset? LastWarned { get; init; }

    /// <summary>Когда SideBar впервые увидел это состояние: опора для 72-часового предупреждения, пока успеха ещё не было.</summary>
    [JsonPropertyName("firstSeen")] public DateTimeOffset? FirstSeen { get; init; }

    /// <summary>Когда в последний раз запускали цикл обновления (успешный или нет): опора для паузы между неудачными циклами.</summary>
    [JsonPropertyName("lastAttempt")] public DateTimeOffset? LastAttempt { get; init; }

    /// <summary>Сколько циклов подряд закончились неудачей (0 после успеха): определяет длину паузы.</summary>
    [JsonPropertyName("failedAttempts")] public int FailedAttempts { get; init; }

    /// <summary>Будущие времена (часы ушли назад) приводит к <paramref name="now"/>: иначе «успех из будущего» блокировал бы цикл.</summary>
    public AppState ClampedTo(DateTimeOffset now) => this with
    {
        LastSuccess = Min(LastSuccess, now),
        LastWarned = Min(LastWarned, now),
        FirstSeen = Min(FirstSeen, now),
        LastAttempt = Min(LastAttempt, now),
    };

    static DateTimeOffset? Min(DateTimeOffset? v, DateTimeOffset now) => v is { } t && t > now ? now : v;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppState))]
internal sealed partial class AppStateJsonContext : JsonSerializerContext;

/// <summary>Хранение <see cref="AppState"/>. Запись атомарна: <c>&lt;file&gt;.tmp</c> + <c>File.Move(overwrite)</c>.</summary>
public sealed class AppStateStore(string filePath)
{
    const int LoadRetries = 4, LoadRetryDelayMs = 200, SaveRetries = 3, SaveRetryDelayMs = 100;

    /// <summary>Нет файла или он битый — пустое состояние. Не бросает.</summary>
    public AppState Load()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(filePath)) return new AppState();
                var bytes = File.ReadAllBytes(filePath);
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) bytes = bytes[3..];   // BOM
                return JsonSerializer.Deserialize(bytes, AppStateJsonContext.Default.AppState) ?? new AppState();
            }
            catch (IOException e) when (attempt < LoadRetries && e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(LoadRetryDelayMs);   // антивирус/индексатор держит файл коротко
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return new AppState();
            }
        }
    }

    /// <summary>Бросает IOException/UnauthorizedAccessException. Не безопасен при параллельных вызовах.</summary>
    public void Save(AppState state)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = filePath + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, state, AppStateJsonContext.Default.AppState);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, filePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < SaveRetries)
            {
                Thread.Sleep(SaveRetryDelayMs);
            }
        }
    }
}
