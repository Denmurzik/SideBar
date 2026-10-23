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

    private static bool Parse(string json, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReleaseInfo? info) =>
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
    [InlineData("01.2.3")]
    [InlineData("1.2")]
    [InlineData("1..3")]
    [InlineData("V1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1000000000.0.0")]
    public void Garbage_tag_is_rejected(string tag)
    {
        Assert.False(AppVersion.TryParse(tag, out _));
    }

    [Fact]
    public void Max_nine_digit_component_is_accepted()
    {
        Assert.Equal(new Version(999999999, 0, 0), V("999999999.0.0"));
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
        // часы ушли назад: lastCheck в будущем -> проверяем
        Assert.True(UpdatePolicy.ShouldCheck(T0.AddHours(5), T0, T0.AddHours(9)));
    }

    [Fact]
    public void Started_in_the_future_means_startup_delay_already_elapsed()
    {
        Assert.True(UpdatePolicy.ShouldCheck(T0, T0.AddHours(1), null));
        Assert.True(UpdatePolicy.ShouldCheck(T0.AddSeconds(5), T0.AddDays(3), null));
        Assert.False(UpdatePolicy.ShouldCheck(T0, T0.AddHours(1), T0.AddHours(-1)));
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
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com@evil.example/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com.evil.example/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://user@github.com/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com:8443/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0 & calc"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/\"v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0\x"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/é"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/other/repo/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/"}""")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"https://github.com/Denmurzik/SideBar/releases/"}""")]
    [InlineData("""{"tag_name":123,"html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v1.0.0"}""")]
    [InlineData("""{"tag_name":{"a":1},"html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v1.0.0"}""")]
    public void Non_github_html_url_is_rejected(string json)
    {
        Assert.False(Parse(json, out _));
    }

    [Theory]
    [InlineData("""{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""")]
    [InlineData("""{"html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":"nope","html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""{"tag_name":null,"html_url":"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Not_found_body_is_rejected(string json)
    {
        Assert.False(Parse(json, out _));
    }

    [Fact]
    public void Backslash_in_url_is_rejected_by_url_check_not_by_json()
    {
        const string ok = "https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0";
        Assert.True(Parse("{\"tag_name\":\"v0.2.0\",\"html_url\":\"" + ok + "\"}", out _));
        var bs = (char)92;
        var json = "{\"tag_name\":\"v0.2.0\",\"html_url\":\"" + ok + bs + bs + "x\"}"; // JSON-экранирование: два слэша -> один '\'
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
            Assert.Contains(bs, doc.RootElement.GetProperty("html_url").GetString()!);
        Assert.False(Parse(json, out _));
    }

    [Fact]
    public void Exactly_one_mib_is_accepted_and_one_more_byte_is_not()
    {
        const string head = "{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0\",\"body\":\"";
        const string tail = "\"}";
        var pad = new string('x', ReleaseInfo.MaxJsonBytes - head.Length - tail.Length);
        Assert.True(Parse(head + pad + tail, out _));
        Assert.False(Parse(head + pad + "x" + tail, out _));
    }

    [Fact]
    public void Deep_nesting_is_rejected_without_throwing()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        Assert.False(Parse("{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0\",\"x\":" + deep + "}", out _));
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var pad = new string('x', 1024 * 1024);
        Assert.False(Parse("{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://github.com/Denmurzik/SideBar/releases/tag/v0.2.0\",\"body\":\"" + pad + "\"}", out _));
    }
}

public class UpdateOutcomeTests
{
    private static readonly Version Cur = new(0, 1, 0);
    private static byte[] Body(string tag) => System.Text.Encoding.UTF8.GetBytes(
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/Denmurzik/SideBar/releases/tag/{tag}\"}}");

    [Fact]
    public void Newer_release_is_available()
    {
        var o = UpdateOutcome.Evaluate(200, Body("v0.2.0"), Cur);
        Assert.Equal(UpdateKind.Available, o.Kind);
        Assert.Equal(new Version(0, 2, 0), o.Release!.Version);
        Assert.Equal("update: available", o.LogLine);
    }

    [Fact]
    public void Same_release_is_up_to_date() =>
        Assert.Equal(UpdateKind.UpToDate, UpdateOutcome.Evaluate(200, Body("v0.1.0"), Cur).Kind);

    [Fact]
    public void Private_repo_404_is_not_found_and_logged_as_such()
    {
        var o = UpdateOutcome.Evaluate(404, "{\"message\":\"Not Found\"}"u8, Cur);
        Assert.Equal(UpdateKind.NotFound, o.Kind);
        Assert.Equal("update: not available (404)", o.LogLine);
    }

    [Fact]
    public void Server_error_and_garbage_are_failed_without_body_in_log()
    {
        var e = UpdateOutcome.Evaluate(500, "secret"u8, Cur);
        Assert.Equal(UpdateKind.Failed, e.Kind);
        Assert.Equal("update: failed (http 500)", e.LogLine);
        var g = UpdateOutcome.Evaluate(200, "not json"u8, Cur);
        Assert.Equal(UpdateKind.Failed, g.Kind);
        Assert.Equal("update: failed (parse)", g.LogLine);
    }

    [Fact]
    public void Older_release_is_not_available() =>
        Assert.Equal(UpdateKind.UpToDate, UpdateOutcome.Evaluate(200, Body("v0.0.9"), Cur).Kind);

    [Fact]
    public void State_store_roundtrips_and_tolerates_garbage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = Path.Combine(dir, "update-state.json");
            var store = new UpdateStateStore(p);
            Assert.Null(store.LastCheck);
            var t = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
            store.SaveLastCheck(t);
            Assert.Equal(t, new UpdateStateStore(p).LastCheck);
            File.WriteAllText(p, "{{{");
            Assert.Null(store.LastCheck);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
