using SideBar.Core.Nearby;

namespace SideBar.Core.Settings;

public enum Language { Auto, Russian, English }

public enum EdgeOpenDelay { Ms0, Ms150, Ms300 }

public enum ShelfRetention { Never, Day1, Days7 }

/// <summary>
/// Настройки приложения. Пути и имя ПК — <c>null</c> = «по умолчанию»: значение по умолчанию подставляют потребители, не хранилище.
/// </summary>
public sealed record AppSettings
{
    public Language Language { get; init; } = Language.Auto;
    public bool CheckForUpdates { get; init; } = true;
    public Visibility Visibility { get; init; } = Visibility.Hidden;
    /// <summary>null — имя компьютера.</summary>
    public string? PcName { get; init; }
    /// <summary>null — «Загрузки».</summary>
    public string? ReceiveFolder { get; init; }
    /// <summary>null — стандартный путь официального Quick Share.</summary>
    public string? OfficialExePath { get; init; }
    public EdgeOpenDelay EdgeOpenDelay { get; init; } = EdgeOpenDelay.Ms150;
    public bool BounceOnIncoming { get; init; } = true;
    public bool ShowInFullscreen { get; init; }
    public ShelfRetention ShelfRetention { get; init; } = ShelfRetention.Never;

    public int EdgeOpenDelayMs => EdgeOpenDelay switch { EdgeOpenDelay.Ms0 => 0, EdgeOpenDelay.Ms300 => 300, _ => 150 };
}
