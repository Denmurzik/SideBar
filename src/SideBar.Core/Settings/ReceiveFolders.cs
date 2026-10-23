namespace SideBar.Core.Settings;

/// <summary>Куда писать новую принятую передачу: <paramref name="Path"/>, <paramref name="FellBack"/> — выбранная в настройках папка недоступна.</summary>
public readonly record struct ReceiveTarget(string Path, bool FellBack);

/// <summary>
/// Папка для принятых файлов. Приоритет: переопределение окружением (SIDEBAR_DOWNLOADS_DIR, только абсолютный путь) → папка из настроек →
/// «Загрузки». Папка из настроек не существует (удалена, сетевой диск не отвечает) или путь не абсолютный — «Загрузки».
/// Применяется только к новым передачам: вызывается при старте каждой.
/// </summary>
public static class ReceiveFolders
{
    public static ReceiveTarget Resolve(string? envOverride, string? configured, Func<string> downloads, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrEmpty(envOverride) && Path.IsPathFullyQualified(envOverride)) return new(envOverride, false);
        if (string.IsNullOrWhiteSpace(configured)) return new(downloads(), false);
        if (Path.IsPathFullyQualified(configured) && directoryExists(configured)) return new(configured, false);
        return new(downloads(), true);
    }
}

/// <summary>Имя этого ПК в рекламе и в данных отправки: из настроек, иначе имя компьютера.</summary>
public static class DeviceNames
{
    public static string Resolve(AppSettings settings, string machineName) =>
        SettingsStore.SanitizePcName(settings.PcName) ?? machineName;
}
