using Microsoft.Win32;
using SideBar.Core;

namespace SideBar.Core.Tests;

public sealed class AutostartFixture : IDisposable
{
    public const string KeyPath = @"Software\SideBar.Tests\Run";

    public AutostartFixture() => Cleanup();
    public void Dispose() => Cleanup();

    static void Cleanup() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\SideBar.Tests", throwOnMissingSubKey: false);
}

public class AutostartTests : IClassFixture<AutostartFixture>
{
    const string Name = "SideBar";

    // тесты в классе идут последовательно, поэтому каждый начинает с чистого ключа
    static Autostart Fresh()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\SideBar.Tests", throwOnMissingSubKey: false);
        return new Autostart(AutostartFixture.KeyPath, Name);
    }

    static string Quote(string path) => "\"" + path + "\"";

    static string? Raw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartFixture.KeyPath);
        return key?.GetValue(Name) as string;
    }

    [Fact]
    public void Disabled_by_default() => Assert.False(Fresh().IsEnabled);

    [Fact]
    public void Enable_writes_quoted_path()
    {
        var a = Fresh();
        a.Enable(@"C:\A B\SideBar.exe");
        Assert.True(a.IsEnabled);
        Assert.Equal(Quote(@"C:\A B\SideBar.exe"), Raw());
    }

    [Fact]
    public void Disable_removes_value()
    {
        var a = Fresh();
        a.Enable(@"C:\SideBar.exe");
        a.Disable();
        Assert.False(a.IsEnabled);
        Assert.Null(Raw());
    }

    [Fact]
    public void RepairPath_rewrites_stale_path_only_when_enabled()
    {
        var a = Fresh();
        a.RepairPath(@"C:\New\SideBar.exe");
        Assert.Null(Raw());                       // выключен — ничего не появилось

        a.Enable(@"C:\Old\SideBar.exe");
        a.RepairPath(@"C:\New\SideBar.exe");
        Assert.Equal(Quote(@"C:\New\SideBar.exe"), Raw());
    }
}
