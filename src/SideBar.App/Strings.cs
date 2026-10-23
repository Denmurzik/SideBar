namespace SideBar;

/// <summary>Строки UI (RU; EN — в M7).</summary>
static class Strings
{
    public const string AppName = "SideBar";
    public const string Autostart = "Запускать при входе в Windows";
    public const string Exit = "Выход";
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
