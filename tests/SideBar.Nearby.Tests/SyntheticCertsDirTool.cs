using static SideBar.Nearby.Tests.CertFixtures;

namespace SideBar.Nearby.Tests;

/// <summary>
/// Not a test: skipped unless SIDEBAR_WRITE_SYNTHETIC_CERTS=&lt;new or empty dir&gt; is set. Then it writes two Sharing directories
/// for the app's e2e: &lt;dir&gt;/app (SIDEBAR_CERTS_DIR of the app) and &lt;dir&gt;/phone (CLI <c>--self --dir</c>). Each device has its
/// own SELF_SHARE certificate and knows the other's public certificate; valid from now. Never writes under the real
/// %LOCALAPPDATA%\Google and never into a non-empty directory.
/// </summary>
public class SyntheticCertsDirTool
{
    private const string Variable = "SIDEBAR_WRITE_SYNTHETIC_CERTS";

    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };

    [Fact(SkipUnless = nameof(Enabled), Skip = "Tool for the e2e: set SIDEBAR_WRITE_SYNTHETIC_CERTS to a new or empty directory")]
    public void Writes_synthetic_sharing_dirs()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable(Variable)!);
        var google = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google"));
        Assert.False(
            root.Equals(google, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(google + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Refusing to write under the real Google data directory");
        Assert.True(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "The target directory must be new or empty");

        var offset = DateTimeOffset.UtcNow - Now;
        var pc = NewDevice("SyntheticPC", offset: offset);
        var phone = NewDevice("SyntheticPhone", offset: offset);
        WriteSharingDir(Path.Combine(root, "app"), [pc.Private], [pc.Public, phone.Public]);
        WriteSharingDir(Path.Combine(root, "phone"), [phone.Private], [phone.Public, pc.Public]);
    }
}
