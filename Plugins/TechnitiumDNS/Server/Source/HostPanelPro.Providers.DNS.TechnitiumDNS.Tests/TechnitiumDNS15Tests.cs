using Microsoft.VisualStudio.TestTools.UnitTesting;
using HostPanelPro.Providers;
using Technitium = HostPanelPro.Providers.DNS.Technitium;

namespace HostPanelPro.Providers.DNS.TechnitiumDNS.Tests;

// Integration tests against a live Technitium DNS Server HTTP API (see TestServer.cs for
// connection details). Not runnable without that server up and reachable.
//
// All tests share one primary zone (created in ClassInitialize, deleted in ClassCleanup)
// for record-level tests, but each test adds/removes its own uniquely-named records within
// it, so tests remain independent of execution order. Tests that themselves exercise zone
// creation/deletion create their own separate, uniquely-named zone instead.
[TestClass]
public class TechnitiumDNS15Tests
{
    private static string _testZoneName;

    private static TechnitiumDNS15 NewProvider()
    {
        var dns = new TechnitiumDNS15();
        dns.ProviderSettings = new ServiceProviderSettings(new[]
        {
            $"ServerUrl={TestServer.ServiceUrl}",
            $"AdminUser={TestServer.AdminUser}",
            $"AdminPassword={TestServer.AdminPassword}",
            "RefreshInterval=01:00:00",
            "RetryDelay=00:15:00",
            "ExpireLimit=7.00:00:00",
            "MinimumTTL=00:05:00",
        });
        return dns;
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(prefix.Length + 9, 30));

    private static string UniqueZoneName(string prefix) => UniqueName(prefix) + ".test";

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _testZoneName = UniqueZoneName("dnstest");

        var dns = NewProvider();
        await dns.AddPrimaryZone(_testZoneName, null);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        var dns = NewProvider();
        if (await dns.ZoneExists(_testZoneName))
            await dns.DeleteZone(_testZoneName);
    }

    [TestMethod]
    public async Task Zone_ExistsCheck_ReflectsRealState()
    {
        var dns = NewProvider();
        Assert.IsTrue(await dns.ZoneExists(_testZoneName));
        Assert.IsFalse(await dns.ZoneExists("this-zone-should-not-exist.invalid"));
    }

    [TestMethod]
    public async Task Zone_GetZones_IncludesTestZone()
    {
        var dns = NewProvider();
        var zones = await dns.GetZones();
        Assert.IsTrue(zones.Contains(_testZoneName));
    }

    // Regression test: Technitium always carries its own built-in zones (root hints,
    // reverse lookup zones, "localhost", etc.) marked "internal" - GetZones() must exclude
    // them (see TechnitiumDNS15.GetZones).
    [TestMethod]
    public async Task Zone_GetZones_ExcludesInternalZones()
    {
        var dns = NewProvider();
        var allZones = await dns.ListZonesAsync();
        var internalZoneNames = allZones.Where(z => z.Internal).Select(z => z.name).ToArray();
        var zones = await dns.GetZones();

        foreach (var name in internalZoneNames)
            Assert.IsFalse(zones.Contains(name), $"Internal zone '{name}' should not be returned by GetZones().");
    }

    [TestMethod]
    public async Task Zone_AddPrimaryZone_CreatesZone()
    {
        var dns = NewProvider();
        string zone = UniqueZoneName("primary");
        await dns.AddPrimaryZone(zone, null);
        try
        {
            Assert.IsTrue(await dns.ZoneExists(zone));
        }
        finally
        {
            await dns.DeleteZone(zone);
        }
        Assert.IsFalse(await dns.ZoneExists(zone));
    }

    // Regression test: with secondary servers specified, AddPrimaryZone must set the zone
    // transfer ACL without throwing (see AddPrimaryZone / Technitium.ZoneTransferOptions).
    [TestMethod]
    public async Task Zone_AddPrimaryZone_WithSecondaryServers_DoesNotThrow()
    {
        var dns = NewProvider();
        string zone = UniqueZoneName("primaryacl");
        await dns.AddPrimaryZone(zone, new[] { "203.0.113.10", "203.0.113.11" });
        try
        {
            Assert.IsTrue(await dns.ZoneExists(zone));
        }
        finally
        {
            await dns.DeleteZone(zone);
        }
    }

    // A secondary zone requires transferring an initial SOA from a reachable, authoritative
    // master during creation itself (verified against a live server - it does not create
    // the zone first and transfer later), so creating one against an address that isn't
    // actually authoritative for the zone must surface as a clean TechnitiumApiException
    // and must not leave a half-created zone behind.
    [TestMethod]
    public async Task Zone_AddSecondaryZone_WithUnreachableMaster_ThrowsTechnitiumApiException()
    {
        var dns = NewProvider();
        string zone = UniqueZoneName("secondary");
        try
        {
            await dns.AddSecondaryZone(zone, new[] { "127.0.0.1" });
            Assert.Fail("Expected AddSecondaryZone to fail when the master isn't authoritative for the zone.");
        }
        catch (Technitium.TechnitiumApiException)
        {
            // expected
        }
        Assert.IsFalse(await dns.ZoneExists(zone));
    }

    [TestMethod]
    public async Task Zone_DeleteZone_RemovesZone()
    {
        var dns = NewProvider();
        string zone = UniqueZoneName("delete");
        await dns.AddPrimaryZone(zone, null);
        Assert.IsTrue(await dns.ZoneExists(zone));

        await dns.DeleteZone(zone);
        Assert.IsFalse(await dns.ZoneExists(zone));
    }

    // The real API rejects creating a zone that already exists, so this must surface as a
    // TechnitiumApiException, not succeed or fail with an unrelated error. Also confirms a
    // failed create doesn't corrupt the shared test zone used by the other tests.
    [TestMethod]
    public async Task Zone_AddPrimaryZone_DuplicateZone_ThrowsTechnitiumApiException()
    {
        var dns = NewProvider();
        try
        {
            await dns.AddPrimaryZone(_testZoneName, null);
            Assert.Fail("Expected AddPrimaryZone to fail for an already-existing zone.");
        }
        catch (Technitium.TechnitiumApiException)
        {
            // expected
        }
    }

    [TestMethod]
    public async Task Soa_UpdateSoaRecord_UpdatesFieldsAndIncrementsSerial()
    {
        var dns = NewProvider();
        var before = await GetSoaRecord(dns, _testZoneName);
        int serialBefore = before.GetInt32("serial");

        string primaryNs = "ns1." + _testZoneName;
        // Technitium returns responsiblePerson in "user@domain" form (verified against a
        // live server), so the input is given in that same form for an exact round trip.
        string responsiblePerson = "hostmaster@" + _testZoneName;
        await dns.UpdateSoaRecord(_testZoneName, _testZoneName, primaryNs, responsiblePerson);

        var after = await GetSoaRecord(dns, _testZoneName);
        Assert.AreEqual(primaryNs, after.GetString("primaryNameServer"));
        Assert.AreEqual(responsiblePerson, after.GetString("responsiblePerson"));
        Assert.AreEqual(serialBefore + 1, after.GetInt32("serial"));
        // The provider's configured refresh/retry/expire/minimum (see NewProvider) must
        // actually be applied to the SOA record.
        Assert.AreEqual(3600, after.GetInt32("refresh"));
        Assert.AreEqual(900, after.GetInt32("retry"));
        Assert.AreEqual(604800, after.GetInt32("expire"));
        Assert.AreEqual(300, after.GetInt32("minimum"));
    }

    // Regression test: GetZoneRecords must exclude SOA - the shared DnsRecord model has no
    // way to represent it (see TechnitiumDNS15.GetZoneRecords).
    [TestMethod]
    public async Task Zone_GetZoneRecords_ExcludesSoaRecord()
    {
        var dns = NewProvider();
        var records = await dns.GetZoneRecords(_testZoneName);
        Assert.IsFalse(records.Any(r => r.RecordType == DnsRecordType.SOA));
    }

    [TestMethod]
    public async Task Record_A_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord { RecordName = UniqueName("a"), RecordType = DnsRecordType.A, RecordData = "203.0.113.20" };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.A && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("203.0.113.20", fetched.RecordData);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }

        var afterDelete = await dns.GetZoneRecords(_testZoneName);
        Assert.IsFalse(afterDelete.Any(r => r.RecordType == DnsRecordType.A && r.RecordName == record.RecordName));
    }

    [TestMethod]
    public async Task Record_AAAA_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord { RecordName = UniqueName("aaaa"), RecordType = DnsRecordType.AAAA, RecordData = "2001:db8::20" };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.AAAA && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("2001:db8::20", fetched.RecordData);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }
    }

    [TestMethod]
    public async Task Record_CNAME_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        string alias = UniqueName("cname");
        var record = new DnsRecord { RecordName = alias, RecordType = DnsRecordType.CNAME, RecordData = "target." + _testZoneName };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.CNAME && r.RecordName == alias);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("target." + _testZoneName, fetched.RecordData);
        }
        finally
        {
            // Regression test: CNAME has no identifying value in the Delete Record call -
            // deleting must still succeed even though record.RecordData isn't sent
            // (see TechnitiumDNS15.BuildTypeParameters).
            await dns.DeleteZoneRecord(_testZoneName, record);
        }

        var afterDelete = await dns.GetZoneRecords(_testZoneName);
        Assert.IsFalse(afterDelete.Any(r => r.RecordType == DnsRecordType.CNAME && r.RecordName == alias));
    }

    [TestMethod]
    public async Task Record_MX_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord { RecordName = UniqueName("mx"), RecordType = DnsRecordType.MX, RecordData = "mail." + _testZoneName, MxPriority = 10 };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.MX && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("mail." + _testZoneName, fetched.RecordData);
            Assert.AreEqual(10, fetched.MxPriority);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }
    }

    [TestMethod]
    public async Task Record_TXT_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord { RecordName = UniqueName("txt"), RecordType = DnsRecordType.TXT, RecordData = "hello from HostPanelPro tests" };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.TXT && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("hello from HostPanelPro tests", fetched.RecordData);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }
    }

    [TestMethod]
    public async Task Record_SRV_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord
        {
            RecordName = "_sip._tcp." + UniqueName("srv"),
            RecordType = DnsRecordType.SRV,
            RecordData = "sipserver." + _testZoneName,
            SrvPriority = 10,
            SrvWeight = 5,
            SrvPort = 5060,
        };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.SRV && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("sipserver." + _testZoneName, fetched.RecordData);
            Assert.AreEqual(10, fetched.SrvPriority);
            Assert.AreEqual(5, fetched.SrvWeight);
            Assert.AreEqual(5060, fetched.SrvPort);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }
    }

    [TestMethod]
    public async Task Record_NS_AddGetDelete_RoundTrips()
    {
        var dns = NewProvider();
        var record = new DnsRecord { RecordName = UniqueName("ns"), RecordType = DnsRecordType.NS, RecordData = "ns1.example.com" };
        await dns.AddZoneRecord(_testZoneName, record);
        try
        {
            var records = await dns.GetZoneRecords(_testZoneName);
            var fetched = records.FirstOrDefault(r => r.RecordType == DnsRecordType.NS && r.RecordName == record.RecordName);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("ns1.example.com", fetched.RecordData);
        }
        finally
        {
            await dns.DeleteZoneRecord(_testZoneName, record);
        }
    }

    [TestMethod]
    public async Task Record_AddZoneRecords_And_DeleteZoneRecords_Batch_RoundTrips()
    {
        var dns = NewProvider();
        var records = new[]
        {
            new DnsRecord { RecordName = UniqueName("batch1"), RecordType = DnsRecordType.A, RecordData = "203.0.113.30" },
            new DnsRecord { RecordName = UniqueName("batch2"), RecordType = DnsRecordType.A, RecordData = "203.0.113.31" },
        };

        await dns.AddZoneRecords(_testZoneName, records);
        try
        {
            var fetched = await dns.GetZoneRecords(_testZoneName);
            Assert.IsTrue(fetched.Any(r => r.RecordType == DnsRecordType.A && r.RecordName == records[0].RecordName));
            Assert.IsTrue(fetched.Any(r => r.RecordType == DnsRecordType.A && r.RecordName == records[1].RecordName));
        }
        finally
        {
            await dns.DeleteZoneRecords(_testZoneName, records);
        }

        var afterDelete = await dns.GetZoneRecords(_testZoneName);
        Assert.IsFalse(afterDelete.Any(r => r.RecordName == records[0].RecordName));
        Assert.IsFalse(afterDelete.Any(r => r.RecordName == records[1].RecordName));
    }

    [TestMethod]
    public async Task Api_LoginAsync_WithInvalidCredentials_ThrowsTechnitiumApiException()
    {
        var api = new Technitium.Api(TestServer.ServiceUrl, "invalid-user", "invalid-password");
        try
        {
            await api.LoginAsync();
            Assert.Fail("Expected LoginAsync to fail for invalid credentials.");
        }
        catch (Technitium.TechnitiumApiException)
        {
            // expected
        }
    }

    private static async Task<Technitium.DnsRecordEntry> GetSoaRecord(TechnitiumDNS15 dns, string zoneName)
    {
        var records = await dns.GetRecordsAsync(zoneName, zoneName);
        return records.First(r => r.type == "SOA");
    }
}
