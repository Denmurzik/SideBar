using Microsoft.Win32;

namespace SideBar.Core;

/// <summary>Автозапуск через HKCU\...\Run: значение — полный путь к exe в кавычках.</summary>
public sealed class Autostart(string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "SideBar")
{
    public bool IsEnabled => Read() is not null;

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
        key.SetValue(valueName, Quote(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>Если автозапуск включён, но путь устарел (exe перенесён/обновлён) — переписывает на текущий.</summary>
    public void RepairPath(string exePath)
    {
        var current = Read();
        if (current is null) return;
        var expected = Quote(exePath);
        if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) Enable(exePath);
    }

    string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return key?.GetValue(valueName) as string;
    }

    static string Quote(string path) => "\"" + path + "\"";
}
