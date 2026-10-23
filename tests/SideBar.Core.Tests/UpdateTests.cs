using System.Text;
using SideBar.Core.Updates;

namespace SideBar.Core.Tests;

public class UpdateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static Version V(string s)
    {
        Assert.True(AppVersion.TryParse(s, out var v));
        return v;
    }

    private static bool Parse(string json, out ReleaseInfo info) =>
        ReleaseInfo.TryParse(Encoding.UTF8.GetBytes(json), out info);

    [Fact]
    public void Version_parses_v_prefix_and_build_suffix()
    {
        Assert.Equal(new Version(0, 1, 0), V("0.1.0"));
        Assert.Equal(new Version(0, 1, 0), V("v0.1.0"));
        Assert.Equal(new Version(0, 1, 0), V("0.1.0+abc123"));
        Assert.Equal(new Version(0, 1, 0), V("v0.1.0+abc123"));
        Assert.Equal(new Version(0, 1, 0), V("0.1.0-beta"));
    }

    [Fact]
    public void Same_version_with_build_metadata_is_not_newer()
    {
        Assert.False(AppVersion.IsNewer(V("0.1.0+abc123"), V("v0.1.0")));
        Assert.False(AppVersion.IsNewer(V("0.1.0"), V("0.1.0-beta")));
        Assert.False(AppVersion.IsNewer(V("0.1.0"), V("0.1.0")));
        Assert.False(AppVersion.IsNewer(V("0.2.0"), V("0.1.9")));
    }

    [Fact]
    public void Newer_patch_is_newer()
    {
        Assert.True(AppVersion.IsNewer(V("0.1.0+abc123"), V("v0.1.1")));
        Assert.True(AppVersion.IsNewer(V("0.1.9"), V("0.2.0")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.-3")]
    [InlineData("+abc")]
    [InlineData("vv1.2.3")]
    [InlineData("99999999999.0.0")]
    public void Garbage_tag_is_rejected(string tag)
    {
        Assert.False(AppVersion.TryParse(tag, out _));
    }

    [Fact]
    public void Null_tag_is_rejected()
    {
        Assert.False(AppVersion.TryParse(null, out _));
    }

    [Fact]
    public void Checks_at_most_once_per_24h_and_not_in_first_2_min()
    {
        Assert.False(UpdatePolicy.ShouldCheck(T0.AddSeconds(119), T0, null));
        Assert.True(UpdatePolicy.ShouldCheck(T0.AddMinutes(2), T0, null));
        Assert.False(UpdatePolicy.ShouldCheck(T0.AddHours(5), T0, T0.AddHours(4)));
        Assert.False(UpdatePolicy.ShouldCheck(T0.AddHours(28).AddSeconds(-1), T0, T0.AddHours(4)));
        Assert.True(UpdatePolicy.ShouldCheck(T0.AddHours(28), T0, T0.AddHours(4)));
        // часы ушли назад: lastCheck в будущем -> не проверяем
        Assert.False(UpdatePolicy.ShouldCheck(T0.AddHours(5), T0, T0.AddHours(9)));
    }

    [Fact]
    public void Release_json_parses_tag_and_url()
    {
        Assert.True(Parse("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0","name":"x"}""", out var r));
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.Equal("https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0", r.HtmlUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://evil.example/releases/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"http://github.com/a/b"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com.evil.example/a"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"file:///C:/x.exe"}""")]
    [InlineData("""{"tag_name":"v0.2.0"}""")]
    public void Non_github_html_url_is_rejected(string json)
    {
        Assert.False(Parse(json, out _));
    }

    [Theory]
    [InlineData("""{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""")]
    [InlineData("""{"html_url":"https://github.com/a/b"}""")]
    [InlineData("""{"tag_name":"nope","html_url":"https://github.com/a/b"}""")]
    [InlineData("""{"tag_name":null,"html_url":"https://github.com/a/b"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Not_found_body_is_rejected(string json)
    {
        Assert.False(Parse(json, out _));
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var pad = new string('x', 1024 * 1024);
        Assert.False(Parse("{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/a/b\",\"body\":\"" + pad + "\"}", out _));
    }
}
