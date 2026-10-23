namespace SideBar.Core.Localization;

/// <summary>Таблицы строк UI. Язык выбирается снаружи (<see cref="Settings.UiLanguage"/>): русский — "ru", всё остальное — английский.</summary>
public static class UiTexts
{
    public static UiText Ru => UiTextRu.Table;
    public static UiText En => UiTextEn.Table;

    public static UiText For(string twoLetterLanguage) =>
        string.Equals(twoLetterLanguage, "ru", StringComparison.OrdinalIgnoreCase) ? Ru : En;
}
