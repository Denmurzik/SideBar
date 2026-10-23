namespace SideBar.Core.Settings;

/// <summary>Единственная точка чтения настройки «Язык» (M7: локализация): Авто идёт за языком интерфейса Windows.</summary>
public static class UiLanguage
{
    /// <summary>
    /// Russian или English, никогда Auto. Авто: двухбуквенный код языка интерфейса Windows ("ru") — Russian, любой другой — English.
    /// Код берётся не из CultureInfo: в InvariantGlobalization она всегда инвариантная.
    /// </summary>
    public static Language Resolve(AppSettings settings, string osLanguage) => settings.Language switch
    {
        Language.Russian => Language.Russian,
        Language.English => Language.English,
        _ => osLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase) ? Language.Russian : Language.English,
    };

    /// <summary>Код языка из LANGID Windows (GetUserDefaultUILanguage): основной язык (младшие 10 бит) 0x19 — русский, остальное считаем английским.</summary>
    public static string FromLangId(ushort langId) => (langId & 0x3FF) == 0x19 ? "ru" : "en";
}
