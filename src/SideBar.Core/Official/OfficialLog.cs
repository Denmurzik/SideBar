namespace SideBar.Core.Official;

public readonly record struct LogScan(bool MarkerSeen, bool UserActivity);

/// <summary>Расписание попыток закрыть запущенный нами nearby_share: сразу, затем через 2 мин, всего 3; процесс не убиваем.</summary>
public static class CloseRetryPolicy
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    /// <summary>Пауза перед попыткой <paramref name="attempt"/> (с 1); null — попытки кончились.</summary>
    public static TimeSpan? DelayBefore(int attempt) =>
        attempt < 1 || attempt > MaxAttempts ? null : attempt == 1 ? TimeSpan.Zero : RetryDelay;
}

/// <summary>
/// Читает хвост логов официального приложения (<c>nearby_share*.log</c>) от смещений, снятых до запуска.
/// Ротация (новый <c>nearby_share_&lt;ts&gt;.log</c>) учитывается: файл без смещения читается с нуля. Содержимое наружу не отдаётся — только флаги.
/// </summary>
public sealed class LogTailScanner(string logDir)
{
    public const string PublishMarker = "PublishDevice succeeded";
    const string Pattern = "nearby_share*.log";

    /// <summary>Длины текущих логов; пустой словарь, если каталога нет; null — каталог не прочитать (без базы Scan принял бы старые логи за новые).</summary>
    public Dictionary<string, long>? Snapshot()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.GetFiles(logDir, Pattern))
                map[f] = new FileInfo(f).Length;
        }
        catch (DirectoryNotFoundException) { map.Clear(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return map;
    }

    /// <summary>Флаги по тексту, появившемуся после снимка (читаем всё от смещения каждый раз — маркер на границе чтений не теряется).</summary>
    public LogScan Scan(IReadOnlyDictionary<string, long> before)
    {
        var marker = false;
        var activity = false;
        string[] files;
        try { files = Directory.GetFiles(logDir, Pattern); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
        foreach (var f in files)
        {
            var text = ReadFrom(f, before.TryGetValue(f, out var off) ? off : 0);
            if (text is null) continue;
            var r = Classify(text);
            marker |= r.MarkerSeen;
            activity |= r.UserActivity;
        }
        return new LogScan(marker, activity);
    }

    /// <summary>
    /// Маркер публикации и признаки того, что приложением начал пользоваться человек: <c>Received WM_COPYDATA</c> или <c>command line:</c>.
    /// Собственная строка запуска <c>We are the only instance, command line:</c> не считается (она пишется при любом старте).
    /// </summary>
    public static LogScan Classify(string text)
    {
        var marker = text.Contains(PublishMarker, StringComparison.Ordinal);
        var activity = text.Contains("Received WM_COPYDATA", StringComparison.Ordinal);
        if (!activity)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("command line:", StringComparison.Ordinal)
                    && !line.Contains("We are the only instance, command line:", StringComparison.Ordinal))
                {
                    activity = true;
                    break;
                }
            }
        }
        return new LogScan(marker, activity);
    }

    static string? ReadFrom(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            if (len < offset) offset = 0;   // файл усечён/перезаписан: читаем с начала
            if (len == offset) return null;
            fs.Position = offset;
            var buf = new byte[len - offset];
            var n = fs.Read(buf, 0, buf.Length);
            return System.Text.Encoding.Latin1.GetString(buf, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
