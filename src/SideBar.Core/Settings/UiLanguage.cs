using System.Globalization;

namespace SideBar.Core.Settings;

/// <summary>Единственная точка чтения настройки «Язык» (M7: локализация): Авто идёт за языком интерфейса Windows.</summary>
public static class UiLanguage
{
    /// <summary>Russian или English, никогда Auto. Авто: язык интерфейса ru — Russian, любой другой — English.</summary>
    public static Language Resolve(AppSettings settings, CultureInfo uiCulture) => settings.Language switch
    {
        Language.Russian => Language.Russian,
        Language.English => Language.English,
        _ => uiCulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? Language.Russian : Language.English,
    };
}
