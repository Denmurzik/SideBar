using static SideBar.Nearby.Tests.CertFixtures;

namespace SideBar.Nearby.Tests;

/// <summary>
/// Not a test: skipped unless SIDEBAR_WRITE_SYNTHETIC_CERTS=&lt;new or empty dir&gt; is set. Then it writes two Sharing directories
/// for the app's e2e: &lt;dir&gt;/app (SIDEBAR_CERTS_DIR of the app) and &lt;dir&gt;/phone (CLI <c>--self --dir</c>). Each device has its
/// own SELF_SHARE certificate and knows the other's public certificate; valid from now. Only writes under %TEMP% and never into a
/// non-empty directory.
/// </summary>
public class SyntheticCertsDirTool
{
    private const string Variable = "SIDEBAR_WRITE_SYNTHETIC_CERTS";

    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };

    [Fact(SkipUnless = nameof(Enabled), Skip = "Tool for the e2e: set SIDEBAR_WRITE_SYNTHETIC_CERTS to a new or empty directory")]
    public void Writes_synthetic_sharing_dirs()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable(Variable)!);
        // Lexical check on the full path; %TEMP% is the only place the tool writes to (no UNC, junction or SUBST tricks
        // into the real Google data directory).
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        Assert.True(
            root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Refusing to write outside %TEMP%");
        Assert.True(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "The target directory must be new or empty");

        var offset = DateTimeOffset.UtcNow - Now;
        var pc = NewDevice("SyntheticPC", offset: offset);
        var phone = NewDevice("SyntheticPhone", offset: offset);
        WriteSharingDir(Path.Combine(root, "app"), [pc.Private], [pc.Public, phone.Public]);
        WriteSharingDir(Path.Combine(root, "phone"), [phone.Private], [phone.Public, pc.Public]);
    }
}
