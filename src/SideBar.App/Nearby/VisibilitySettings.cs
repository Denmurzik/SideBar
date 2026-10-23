using Microsoft.Win32;
using SideBar.Core.Nearby;

namespace SideBar.Sharing;

/// <summary>Выбранный режим видимости (Hidden или OwnDevices; "Все" не сохраняется) в HKCU\Software\SideBar, значение VisibilityMode.</summary>
static class VisibilitySettings
{
    const string KeyPath = @"Software\SideBar";
    const string ValueName = "VisibilityMode";

#if DEBUG
    // автотест: синтетические сертификаты или явный режим; реестр пользователя не читается и не пишется
    static string? Override => Environment.GetEnvironmentVariable("SIDEBAR_VISIBILITY");
    static bool Isolated => Override is { Length: > 0 } || Environment.GetEnvironmentVariable("SIDEBAR_CERTS_DIR") is { Length: > 0 };
#endif

    public static Visibility? Load()
    {
#if DEBUG
        if (Isolated)
            return Override switch { "hidden" => Visibility.Hidden, "own" => Visibility.OwnDevices, _ => null };
#endif
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return (key?.GetValue(ValueName) as string) switch
            {
                nameof(Visibility.Hidden) => Visibility.Hidden,
                nameof(Visibility.OwnDevices) => Visibility.OwnDevices,
                _ => null,
            };
        }
        catch (Exception e)
        {
            Log.Write("visibility load failed: " + Log.Describe(e));
            return null;
        }
    }

    public static void Save(Visibility mode)
    {
#if DEBUG
        if (Isolated) return;
#endif
        if (mode == Visibility.Everyone) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, mode.ToString(), RegistryValueKind.String);
        }
        catch (Exception e)
        {
            Log.Write("visibility save failed: " + Log.Describe(e));
        }
    }
}
