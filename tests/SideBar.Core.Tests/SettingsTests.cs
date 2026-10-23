using SideBar.Core.Nearby;
using SideBar.Core.Settings;

namespace SideBar.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_when_missing()
    {
        var s = new SettingsStore(FilePath).Current;
        Assert.Equal(new AppSettings(), s);
        Assert.Equal(Language.Auto, s.Language);
        Assert.True(s.CheckForUpdates);
        Assert.Equal(Visibility.Hidden, s.Visibility);
        Assert.Null(s.PcName);
        Assert.Null(s.ReceiveFolder);
        Assert.Null(s.OfficialExePath);
        Assert.Equal(EdgeOpenDelay.Ms150, s.EdgeOpenDelay);
        Assert.Equal(150, s.EdgeOpenDelayMs);
        Assert.True(s.BounceOnIncoming);
        Assert.False(s.ShowInFullscreen);
        Assert.Equal(ShelfRetention.Never, s.ShelfRetention);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Round_trip_preserves_all_fields()
    {
        var expected = new AppSettings
        {
            Language = Language.English,
            CheckForUpdates = false,
            PcName = "Мой ПК",
            ReceiveFolder = @"D:\Приём",
            OfficialExePath = @"D:\NS\nearby_share.exe",
            EdgeOpenDelay = EdgeOpenDelay.Ms300,
            BounceOnIncoming = false,
            ShowInFullscreen = true,
            ShelfRetention = ShelfRetention.Days7,
        };
        new SettingsStore(FilePath).Update(_ => expected);
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(expected, new SettingsStore(FilePath).Current);
    }

    [Fact]
    public void Corrupt_file_quarantined_and_defaults_used()
    {
        File.WriteAllText(FilePath, "{ not json !!");
        var store = new SettingsStore(FilePath);
        Assert.Equal(new AppSettings(), store.Current);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "settings.bad-*.json"));
        store.Update(s => s with { ShowInFullscreen = true });
        Assert.True(new SettingsStore(FilePath).Current.ShowInFullscreen);
    }

    [Fact]
    public void Unknown_fields_ignored()
    {
        File.WriteAllText(FilePath, """{ "future": { "x": 1 }, "language": "Russian", "other": [1,2] }""");
        var s = new SettingsStore(FilePath).Current;
        Assert.Equal(Language.Russian, s.Language);
        Assert.Empty(Directory.GetFiles(_dir, "settings.bad-*.json"));
    }

    [Fact]
    public void Update_raises_Changed_once()
    {
        var store = new SettingsStore(FilePath);
        var raised = new List<AppSettings>();
        store.Changed += raised.Add;
        store.Update(s => s with { BounceOnIncoming = false });
        Assert.Single(raised);
        Assert.False(raised[0].BounceOnIncoming);
        Assert.Same(store.Current, raised[0]);
        store.Update(s => s with { BounceOnIncoming = false });   // без изменений — события нет
        Assert.Single(raised);
    }

    [Fact]
    public void Pc_name_sanitized_and_capped_at_32()
    {
        Assert.Equal("Ноутбук Ивана", SettingsStore.SanitizePcName("  Ноутбук\u0000\r\n  Ивана\u202E "));
        Assert.Equal(32, SettingsStore.SanitizePcName(new string('x', 200))!.Length);
        var emoji = SettingsStore.SanitizePcName(new string('a', 31) + "😀😀")!;   // пара не рвётся на границе
        Assert.Equal(new string('a', 31), emoji);
        Assert.Null(SettingsStore.SanitizePcName(" \u0000\u202E "));
        Assert.Null(SettingsStore.SanitizePcName(null));

        var store = new SettingsStore(FilePath);
        store.Update(s => s with { PcName = new string('y', 200) });
        Assert.Equal(32, store.Current.PcName!.Length);
        store.Update(s => s with { PcName = "\u0007" });
        Assert.Null(store.Current.PcName);
    }

    [Fact]
    public void Out_of_range_enum_values_fall_back_to_default()
    {
        File.WriteAllText(FilePath, """{ "language": "Klingon", "visibility": "99", "edgeOpenDelay": "7", "shelfRetention": "", "checkForUpdates": false }""");
        var s = new SettingsStore(FilePath).Current;
        Assert.Equal(Language.Auto, s.Language);
        Assert.Equal(Visibility.Hidden, s.Visibility);
        Assert.Equal(EdgeOpenDelay.Ms150, s.EdgeOpenDelay);
        Assert.Equal(ShelfRetention.Never, s.ShelfRetention);
        Assert.False(s.CheckForUpdates);

        var store = new SettingsStore(Path.Combine(_dir, "b.json"));
        store.Update(x => x with { Language = (Language)42, EdgeOpenDelay = (EdgeOpenDelay)(-1) });
        Assert.Equal(Language.Auto, store.Current.Language);
        Assert.Equal(EdgeOpenDelay.Ms150, store.Current.EdgeOpenDelay);
    }

    [Fact]
    public void MigrateVisibility_takes_registry_value_only_over_default()
    {
        var def = new AppSettings();
        Assert.Equal(def, SettingsStore.MigrateVisibility(def, Visibility.Everyone));   // «Все» не переносится
        Assert.Equal(def, SettingsStore.MigrateVisibility(def, null));
        Assert.Equal(def, SettingsStore.MigrateVisibility(def, (Visibility)99));
        var chosen = def with { Visibility = Visibility.Everyone };
        Assert.Equal(chosen, SettingsStore.MigrateVisibility(chosen, Visibility.Hidden));
    }
}
