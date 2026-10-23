using SideBar.Core.Nearby;
using SideBar.Core.Settings;

namespace SideBar.Core.Tests;

public sealed class SettingsRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsRobustnessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Parallel_updates_lose_nothing_and_leave_no_tmp()
    {
        var store = new SettingsStore(FilePath);
        var changed = 0;
        store.Changed += _ => Interlocked.Increment(ref changed);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (var i = 0; i < 15; i++) store.Update(s => s with { OfficialExePath = (s.OfficialExePath ?? "") + "a" });
        });
        Assert.Equal(120, store.Current.OfficialExePath!.Length);
        Assert.Equal(120, changed);
        Assert.Equal(store.Current, new SettingsStore(FilePath).Current);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Reentrant_update_from_Changed_keeps_order()
    {
        var store = new SettingsStore(FilePath);
        var seen = new List<string?>();
        store.Changed += s =>
        {
            seen.Add(s.ReceiveFolder);
            if (s.ReceiveFolder == "1") store.Update(x => x with { ReceiveFolder = "2" });
        };
        store.Update(s => s with { ReceiveFolder = "1" });
        Assert.Equal(["1", "2"], seen);
        Assert.Equal("2", store.Current.ReceiveFolder);
        Assert.Equal("2", new SettingsStore(FilePath).Current.ReceiveFolder);
    }

    [Fact]
    public void Wrong_json_type_resets_only_that_field()
    {
        File.WriteAllText(FilePath, """{ "language": 5, "checkForUpdates": "no", "pcName": 7, "bounceOnIncoming": false, "edgeOpenDelay": "Ms300", "receiveFolder": "D:\\x" }""");
        var s = new SettingsStore(FilePath).Current;
        Assert.Equal(Language.Auto, s.Language);
        Assert.True(s.CheckForUpdates);
        Assert.Null(s.PcName);
        Assert.False(s.BounceOnIncoming);
        Assert.Equal(EdgeOpenDelay.Ms300, s.EdgeOpenDelay);
        Assert.Equal(@"D:\x", s.ReceiveFolder);
        Assert.Empty(Directory.GetFiles(_dir, "settings.bad-*.json"));
    }

    [Fact]
    public void Write_failure_does_not_throw_and_keeps_current()
    {
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));   // «каталог» — на деле файл
        var changed = 0; var failed = 0;
        store.Changed += _ => changed++;
        store.SaveFailed += () => failed++;
        store.Update(s => s with { ShowInFullscreen = true });
        Assert.True(store.Current.ShowInFullscreen);
        Assert.True(store.LastSaveFailed);
        Assert.Equal(1, changed);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void Locked_file_is_not_quarantined_nor_overwritten()
    {
        File.WriteAllText(FilePath, """{ "language": "Russian" }""");
        SettingsStore store;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new SettingsStore(FilePath);
            Assert.Equal(Language.Auto, store.Current.Language);
            Assert.True(store.LastSaveFailed);
            store.Update(s => s with { ShowInFullscreen = true });
            Assert.True(store.Current.ShowInFullscreen);
            Assert.True(store.LastSaveFailed);
        }
        Assert.Empty(Directory.GetFiles(_dir, "settings.bad-*.json"));
        Assert.Contains("Russian", File.ReadAllText(FilePath));
        store.Update(s => s with { BounceOnIncoming = false });   // файл снова читается: подхватываем и пишем поверх прочитанного
        Assert.Equal(Language.Russian, store.Current.Language);
        Assert.False(store.Current.BounceOnIncoming);
        Assert.False(store.LastSaveFailed);
        Assert.Equal(store.Current, new SettingsStore(FilePath).Current);
    }

    [Fact]
    public void Everyone_is_never_persisted()
    {
        File.WriteAllText(FilePath, """{ "visibility": "Everyone" }""");
        Assert.Equal(Visibility.OwnDevices, new SettingsStore(FilePath).Current.Visibility);

        var path = Path.Combine(_dir, "e.json");
        var store = new SettingsStore(path);
        store.Update(s => s with { Visibility = Visibility.Everyone });
        Assert.Equal(Visibility.Everyone, store.Current.Visibility);
        Assert.Equal(Visibility.OwnDevices, store.PersistedVisibility);
        Assert.DoesNotContain("Everyone", File.ReadAllText(path));
        Assert.Equal(Visibility.OwnDevices, new SettingsStore(path).Current.Visibility);
    }

    [Fact]
    public void Unchanged_update_creates_no_file_and_null_change_is_noop()
    {
        var store = new SettingsStore(FilePath);
        var changed = 0;
        store.Changed += _ => changed++;
        store.Update(s => s);
        store.Update(_ => null);
        store.Update(s => s with { PcName = "  " });   // нормализуется в null: не изменение
        Assert.Equal(0, changed);
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("\uFEFF{ broken")]
    [InlineData("[1,2]")]
    public void Unreadable_content_goes_to_quarantine(string content)
    {
        File.WriteAllText(FilePath, content);
        Assert.Equal(new AppSettings(), new SettingsStore(FilePath).Current);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "settings.bad-*.json"));
    }

    [Fact]
    public void File_with_bom_is_read()
    {
        File.WriteAllBytes(FilePath, [0xEF, 0xBB, 0xBF, .. """{"language":"English"}"""u8.ToArray()]);
        Assert.Equal(Language.English, new SettingsStore(FilePath).Current.Language);
        Assert.Empty(Directory.GetFiles(_dir, "settings.bad-*.json"));
    }

    [Fact]
    public void Edits_made_while_file_was_locked_survive_the_reload()
    {
        File.WriteAllText(FilePath, """{ "language": "Russian", "pcName": "Файл" }""");
        SettingsStore store;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new SettingsStore(FilePath);
            store.Update(s => s with { ShowInFullscreen = true });
            store.Update(s => s with { EdgeOpenDelay = EdgeOpenDelay.Ms300 });
            store.Update(s => s with { BounceOnIncoming = false });
            store.Update(s => s with { BounceOnIncoming = true });   // вернули к значению по умолчанию: всё равно правка пользователя
        }
        store.Update(s => s with { CheckForUpdates = false });
        Assert.Equal(Language.Russian, store.Current.Language);   // из файла
        Assert.Equal("Файл", store.Current.PcName);
        Assert.True(store.Current.ShowInFullscreen);              // из памяти, не только последняя правка
        Assert.Equal(EdgeOpenDelay.Ms300, store.Current.EdgeOpenDelay);
        Assert.True(store.Current.BounceOnIncoming);
        Assert.False(store.Current.CheckForUpdates);
        Assert.Equal(store.Current, new SettingsStore(FilePath).Current);
    }
}
