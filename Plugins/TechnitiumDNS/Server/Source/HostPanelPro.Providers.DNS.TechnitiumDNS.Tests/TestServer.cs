namespace HostPanelPro.Providers.DNS.TechnitiumDNS.Tests;

// Connection details for a live Technitium DNS Server instance used by the integration
// tests in this project. These are NOT unit tests - they create and delete real zones and
// records against the configured server.
//
// admin/admin is Technitium's built-in default administrator account (see
// APIDocumentation.md, "Login": "The default password for `admin` user is `admin`."), but
// this test server's password has been changed from that default.
public static class TestServer
{
    public const string ServiceUrl = "http://localhost:5380";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Password12&";
}
