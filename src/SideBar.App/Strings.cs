namespace SideBar;

/// <summary>Строки UI (RU; EN — в M7).</summary>
static class Strings
{
    public const string AppName = "SideBar";
    public const string Autostart = "Запускать при входе в Windows";
    public const string Exit = "Выход";

    public const string VisibilityHidden = "Скрыто";
    public const string VisibilityOwnDevices = "Свои устройства";
    public const string VisibilityEveryone = "Все (10 мин)";
    public const string StackPaused = "Quick Share на паузе — открыт официальный Quick Share";
    public const string OwnMark = "своё";
    /// <summary>Имя своего устройства, если в сертификате его нет: имя из рекламы задаёт пир и сюда не попадает.</summary>
    public const string OwnDevice = "Ваше устройство";
    public const string CertsUnavailable = "Сертификаты Quick Share недоступны — работает только режим „Все“";
    public const string UnknownDevice = "Устройство";
    public const string Accept = "Принять";
    public const string Decline = "Отклонить";
    public const string Open = "Открыть";
    public const string ShowInFolder = "Показать в папке";
    public const string Copy = "Копировать";
    public const string OpenLink = "Открыть ссылку";
    public static string MoreConsents(int n) => $"ещё {n}";
    public const string WifiUnsupported = "Передача Wi-Fi-сетей не поддерживается";
    public const string NotEnoughSpace = "Недостаточно места на диске для приёма";
    public const string SenderCancelled = "Отправитель отменил передачу";
    public const string TimedOut = "Время ожидания истекло, передача отклонена";
    public const string ReceivePublicNetwork = "Сеть общедоступная — приём выключен";
    public const string ReceiveNoLan = "Нет подключения к локальной сети";
    public const string ReceiveFailed = "Не удалось получить данные";

    public static string Files(int n)
    {
        int m100 = n % 100, m10 = n % 10;
        string word = m100 is >= 11 and <= 14 ? "файлов" : m10 == 1 ? "файл" : m10 is >= 2 and <= 4 ? "файла" : "файлов";
        return $"{n} {word}";
    }

    public static string Received(int n) => $"Получено: {Files(n)}";
    public static string ReceivedFrom(string device, int n) => $"{device}: получено {Files(n)}";

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
    public const string SendUnverified = "Не удалось подтвердить устройство";
    public const string SendTimedOut = "Получатель не ответил";
    public const string CloseOfficialApp = SideBar.Core.Nearby.ShareTileModel.OfficialAppHint;
    public const string NothingToSend = "Нечего отправлять";

    public const string RefreshCertsNow = "Обновить сертификаты сейчас";
    public const string RefreshDone = "Сертификаты обновлены";
    public const string RefreshNotInstalled = "Quick Share не установлен";
    public const string RefreshOfficialOpen = "Quick Share уже открыт";
    public const string RefreshDeferred = "Обновление отложено";
    public const string RefreshFailed = "Не удалось обновить";
    public const string CertsStale = "Не удаётся обновить сертификаты Quick Share — откройте официальное приложение Quick Share один раз";
    public const string OfficialResident = "Официальный Quick Share запущен — SideBar на паузе. Закройте его в трее, чтобы SideBar принимал файлы";
    public const string OfficialLeftover = "Обновление сертификатов не завершилось — официальный Quick Share остался открытым. Закройте его в трее, чтобы SideBar принимал файлы";
}
