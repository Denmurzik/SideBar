namespace SideBar;

/// <summary>Строки UI (RU; EN — в M7).</summary>
static class Strings
{
    public const string AppName = "SideBar";
    public const string Autostart = "Запускать при входе в Windows";
    public const string Exit = "Выход";

    public const string VisibilityEveryone = "Видимость для всех — 10 мин";
    public const string UnknownDevice = "Устройство";
    public const string Accept = "Принять";
    public const string Decline = "Отклонить";
    public const string Open = "Открыть";
    public const string ShowInFolder = "Показать в папке";
    public const string Copy = "Копировать";
    public const string WifiUnsupported = "Передача Wi-Fi-сетей не поддерживается";
    public const string NotEnoughSpace = "Недостаточно места на диске для приёма";
    public const string SenderCancelled = "Отправитель отменил передачу";
    public const string TimedOut = "Время ожидания истекло, передача отклонена";
    public const string ReceiveFailed = "Не удалось получить данные";

    public static string Files(int n)
    {
        int m100 = n % 100, m10 = n % 10;
        string word = m100 is >= 11 and <= 14 ? "файлов" : m10 == 1 ? "файл" : m10 is >= 2 and <= 4 ? "файла" : "файлов";
        return $"{n} {word}";
    }

    public static string Received(int n) => $"Получено: {Files(n)}";

    public static string SharedLink(string device, string domain) => $"{device} поделился ссылкой · {domain}";
    public static string SharedText(string device) => $"{device} поделился текстом";
    public const string TransferInterrupted = "Передача прервана";

    public static string Pin(string pin) => $"PIN {pin}";

    public static string WantsToSend(string device, int files, int texts, bool onlyLinks)
    {
        string what = files > 0
            ? texts > 0 ? $"отправить {Files(files)} и текст" : $"отправить {Files(files)}"
            : onlyLinks ? (texts > 1 ? "поделиться ссылками" : "поделиться ссылкой") : "поделиться текстом";
        return $"{device} хочет {what}";
    }
    public const string Searching = "Поиск устройств…";
    public const string SendToDevice = "Отправить на устройство";
    public const string Sent = "Отправлено";
    public const string SendFailed = "Не удалось отправить";
    public const string SendRejected = "Получатель отклонил";
    public const string SendCancelled = "Получатель отменил передачу";
    public static readonly string OnlyFirstFiles = $"Отправлены только первые {Nearby.NearbyService.MaxFiles} файлов";
    public const string SendTimedOut = "Получатель не ответил";
    public const string NothingToSend = "Нечего отправлять";
}
