using static SideBar.Nearby.Tests.CertFixtures;

namespace SideBar.Nearby.Tests;

/// <summary>
/// Not a test: with SIDEBAR_WRITE_SYNTHETIC_CERTS=&lt;empty dir&gt; it writes two Sharing directories for the app's e2e:
/// &lt;dir&gt;/app (SIDEBAR_CERTS_DIR of the app) and &lt;dir&gt;/phone (CLI <c>--self --dir</c>). Each device has its own SELF_SHARE
/// certificate and knows the other's public certificate; valid from now. Without the variable it does nothing.
/// </summary>
public class SyntheticCertsDirTool
{
    [Fact]
    public void Writes_synthetic_sharing_dirs_when_asked()
    {
        if (Environment.GetEnvironmentVariable("SIDEBAR_WRITE_SYNTHETIC_CERTS") is not { Length: > 0 } root) return;
        var offset = DateTimeOffset.UtcNow - Now;
        var pc = NewDevice("SyntheticPC", offset: offset);
        var phone = NewDevice("SyntheticPhone", offset: offset);
        WriteSharingDir(Path.Combine(root, "app"), [pc.Private], [pc.Public, phone.Public]);
        WriteSharingDir(Path.Combine(root, "phone"), [phone.Private], [phone.Public, pc.Public]);
    }
}
