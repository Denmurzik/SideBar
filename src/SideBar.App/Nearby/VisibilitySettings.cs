using Microsoft.Win32;
using SideBar.Core.Nearby;
using SideBar.Core.Settings;

namespace SideBar.Sharing;

/// <summary>
/// Выбранный режим видимости (Hidden или OwnDevices; "Все" не сохраняется) живёт в <see cref="SettingsStore"/>.
/// Значение HKCU\Software\SideBar\VisibilityMode, оставшееся от M4, один раз переносится туда и удаляется.
/// </summary>
static class VisibilitySettings
{
    const string KeyPath = @"Software\SideBar";
    const string ValueName = "VisibilityMode";

#if DEBUG
    // автотест: синтетические сертификаты или явный режим; реестр пользователя не читается и не пишется
    static string? Override => Environment.GetEnvironmentVariable("SIDEBAR_VISIBILITY");
    static bool Isolated => Override is { Length: > 0 } || Environment.GetEnvironmentVariable("SIDEBAR_CERTS_DIR") is { Length: > 0 };
#endif

    /// <summary>Переносит режим из реестра в настройки и удаляет значение; повторно ничего не делает.</summary>
    public static void MigrateLegacy(SettingsStore store)
    {
#if DEBUG
        if (Isolated) return;
#endif
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string raw) return;
            Visibility? legacy = raw switch
            {
                nameof(Visibility.Hidden) => Visibility.Hidden,
                nameof(Visibility.OwnDevices) => Visibility.OwnDevices,
                _ => null,
            };
            store.Update(s => SettingsStore.MigrateVisibility(s, legacy));
            if (store.LastSaveFailed) return;   // значение остаётся в реестре до следующего запуска
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Write("settings: migrated visibility");
        }
        catch (Exception e)
        {
            Log.Write("visibility migration failed: " + Log.Describe(e));
        }
    }

    public static Visibility? Load(SettingsStore store)
    {
#if DEBUG
        if (Isolated)
            return Override switch { "hidden" => Visibility.Hidden, "own" => Visibility.OwnDevices, _ => null };
#endif
        return store.PersistedVisibility;
    }

    public static void Save(SettingsStore store, Visibility mode)
    {
#if DEBUG
        if (Isolated) return;
#endif
        if (mode == Visibility.Everyone) return;
        store.Update(s => s with { Visibility = mode });
    }
}
