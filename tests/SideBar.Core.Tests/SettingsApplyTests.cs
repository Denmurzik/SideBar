using System.Globalization;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Core.Settings;
using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class SettingsApplyTests
{
    static readonly PxRect Monitor = new(0, 0, 1920, 1080);
    static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    static EdgeInput At(int x, int y, long t, bool left = false) => new(x, y, left, false, t);

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(300)]
    public void Edge_open_delay_respected(int delay)
    {
        var m = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.Slide) { OpenDelayMs = delay };
        if (delay == 0)
        {
            Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 0)));
            return;
        }
        Assert.Equal(PanelState.Peek, m.Update(At(1919, 540, 0)));   // касание края: до задержки только Peek
        Assert.Null(m.Update(At(1919, 540, delay - 1)));
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, delay)));
    }

    [Fact]
    public void Edge_open_delay_standard_keeps_old_behaviour_per_mode()
    {
        Assert.Null(new AppSettings().EdgeOpenDelayMs);
        Assert.Equal(0, new AppSettings { EdgeOpenDelay = EdgeOpenDelay.Ms0 }.EdgeOpenDelayMs);
        Assert.Equal(150, new AppSettings { EdgeOpenDelay = EdgeOpenDelay.Ms150 }.EdgeOpenDelayMs);
        var slide = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.Slide) { OpenDelayMs = new AppSettings().EdgeOpenDelayMs };
        Assert.Equal(PanelState.Open, slide.Update(At(1919, 540, 0)));   // Slide: без задержки
        var inPlace = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.InPlace) { OpenDelayMs = new AppSettings().EdgeOpenDelayMs };
        Assert.Null(inPlace.Update(At(1890, 540, 0)));
        Assert.Null(inPlace.Update(At(1890, 540, 149)));
        Assert.Equal(PanelState.Open, inPlace.Update(At(1890, 540, 150)));   // InPlace: 150 мс
    }

    [Fact]
    public void Edge_open_delay_restarts_after_leaving_the_edge()
    {
        var m = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.Slide) { OpenDelayMs = 150 };
        m.Update(At(1919, 540, 0));
        m.Update(At(1890, 540, 100));   // отошли от края, но остались в полосе
        Assert.Null(m.Update(At(1919, 540, 200)));
        Assert.Null(m.Update(At(1919, 540, 349)));
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 350)));
    }

    [Fact]
    public void Edge_open_delay_does_not_apply_to_drag()
    {
        var m = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.Slide) { OpenDelayMs = 300 };
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 0, left: true)));
    }

    [Fact]
    public void Edge_open_delay_sets_InPlace_dwell()
    {
        var m = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.InPlace) { OpenDelayMs = 300 };
        Assert.Null(m.Update(At(1890, 540, 0)));
        Assert.Null(m.Update(At(1890, 540, 299)));
        Assert.Equal(PanelState.Open, m.Update(At(1890, 540, 300)));
        var z = new EdgeStateMachine(new CardLayout(Monitor, 1.0), EdgeMode.InPlace) { OpenDelayMs = 0 };
        Assert.Equal(PanelState.Open, z.Update(At(1890, 540, 0)));
    }

    [Fact]
    public void Receive_dir_falls_back_to_downloads_when_missing()
    {
        string Downloads() => @"C:\Users\u\Downloads";
        var ok = ReceiveFolders.Resolve(null, @"D:\Inbox", Downloads, _ => true);
        Assert.Equal(new ReceiveTarget(@"D:\Inbox", false), ok);
        var missing = ReceiveFolders.Resolve(null, @"D:\Inbox", Downloads, _ => false);
        Assert.Equal(new ReceiveTarget(@"C:\Users\u\Downloads", true), missing);
        Assert.Equal(new ReceiveTarget(@"C:\Users\u\Downloads", true), ReceiveFolders.Resolve(null, @"relativedir", Downloads, _ => true));
        Assert.Equal(new ReceiveTarget(@"C:\Users\u\Downloads", false), ReceiveFolders.Resolve(null, null, Downloads, _ => false));
    }

    [Fact]
    public void Receive_dir_env_override_wins_and_must_be_absolute()
    {
        string Downloads() => @"C:\Users\u\Downloads";
        Assert.Equal(new ReceiveTarget(@"E:\Test", false), ReceiveFolders.Resolve(@"E:\Test", @"D:\Inbox", Downloads, _ => true));
        Assert.Equal(new ReceiveTarget(@"D:\Inbox", false), ReceiveFolders.Resolve(@"rel", @"D:\Inbox", Downloads, _ => true));
    }

    [Fact]
    public void Pc_name_falls_back_to_machine_name()
    {
        Assert.Equal("HOME", DeviceNames.Resolve(new AppSettings(), "HOME"));
        Assert.Equal("Офис", DeviceNames.Resolve(new AppSettings { PcName = "Офис" }, "HOME"));
        Assert.Equal("HOME", DeviceNames.Resolve(new AppSettings { PcName = "   " }, "HOME"));
    }

    [Fact]
    public void Shelf_items_expire_after_retention()
    {
        ShelfItem Item(int days) => new(Guid.NewGuid(), "f", "C:/f", ShelfItemKind.Reference, Now.AddDays(-days));
        var fresh = Item(0);
        var twoDays = Item(2);
        var eightDays = Item(8);
        List<ShelfItem> all = [fresh, twoDays, eightDays];

        Assert.Empty(ShelfRetentionPolicy.ExpiredIds(all, ShelfRetention.Never, Now));
        Assert.Equal([twoDays.Id, eightDays.Id], ShelfRetentionPolicy.ExpiredIds(all, ShelfRetention.Day1, Now));
        Assert.Equal([eightDays.Id], ShelfRetentionPolicy.ExpiredIds(all, ShelfRetention.Days7, Now));
        var exactly = Item(7);
        Assert.Equal([exactly.Id], ShelfRetentionPolicy.ExpiredIds([exactly], ShelfRetention.Days7, Now));   // ровно срок — уже старый
    }

    [Theory]
    [InlineData(Language.Auto, "ru-RU", Language.Russian)]
    [InlineData(Language.Auto, "en-US", Language.English)]
    [InlineData(Language.Auto, "de-DE", Language.English)]
    [InlineData(Language.Russian, "en-US", Language.Russian)]
    [InlineData(Language.English, "ru-RU", Language.English)]
    public void UiLanguage_resolves(Language setting, string culture, Language expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(new AppSettings { Language = setting }, CultureInfo.GetCultureInfo(culture)));
}

public class TrimGateTests
{
    [Fact]
    public void Settings_hold_and_card_do_not_fight()
    {
        var g = new TrimGate();
        Assert.True(g.Allowed);
        Assert.False(g.SetCardShown(true));
        Assert.False(g.Hold());             // окно настроек открылось при открытой карточке
        Assert.False(g.SetCardShown(false)); // карточка скрылась, настройки всё ещё держат
        Assert.True(g.Release());           // окно закрыто, карточка скрыта: можно
        Assert.False(g.SetCardShown(true));
        Assert.False(g.Hold());
        Assert.False(g.Release());          // настройки закрылись при открытой карточке: нельзя
        Assert.True(g.SetCardShown(false));
    }
}
