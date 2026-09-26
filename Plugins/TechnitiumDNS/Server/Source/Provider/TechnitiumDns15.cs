using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;
using Plugin = HostPanelPro.Plugins.TechnitiumDNS;
using HostPanelPro.Providers.DNS.Technitium;
using HostPanelPro.Providers.OS;

namespace HostPanelPro.Providers.DNS;

public class TechnitiumDNS15 : HostingServiceProviderBase, IDnsServerAsync
{
    #region Provider Properties

    public TimeSpan RefreshInterval
    {
        get
        {
            TimeSpan interval = TimeSpan.Zero;
            TimeSpan.TryParse(ProviderSettings[nameof(RefreshInterval)], out interval);
            return interval;
        }
    }

    public TimeSpan RetryDelay
    {
        get
        {
            TimeSpan delay = TimeSpan.Zero;
            TimeSpan.TryParse(ProviderSettings[nameof(RetryDelay)], out delay);
            return delay;
        }
    }

    public TimeSpan ExpireLimit
    {
        get
        {
            TimeSpan limit = TimeSpan.Zero;
            TimeSpan.TryParse(ProviderSettings[nameof(ExpireLimit)], out limit);
            return limit;
        }
    }

    public TimeSpan MinimumTTL
    {
        get
        {
            TimeSpan ttl = TimeSpan.Zero;
            TimeSpan.TryParse(ProviderSettings[nameof(MinimumTTL)], out ttl);
            return ttl;
        }
    }

    public string ServerUrl
    {
        get { return ProviderSettings[nameof(ServerUrl)]; }
    }

    public string AdminUser
    {
        get { return ProviderSettings[nameof(AdminUser)]; }
    }

    public string AdminPassword
    {
        get { return ProviderSettings[nameof(AdminPassword)]; }
    }
    public string ListenIPsRaw => ProviderSettings[nameof(ListenIPs)];
    public IEnumerable<IPAddress> ListenIPs
    {
        get
        {
            var tokens = ListenIPsRaw
                .Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim());
            foreach (var token in tokens)
            {
                IPAddress adr = null;
                if (IPAddress.TryParse(token, out adr))
                {
                    yield return adr;
                }
            }
        }
    }

    public override SettingPair[] GetProviderDefaultSettings() => new SettingPair[]
        {
            new SettingPair("ServerUrl", "http://localhost:5380"),
            new SettingPair("AdminUser", "admin"),
            new SettingPair("NameServers", "ns1.yourdomain.com;ns2.yourdomain.com"),
            new SettingPair("ExpireLimit", "1209600"),
            new SettingPair("MinimumTTL", "86400"),
            new SettingPair("RefreshInterval", "3600"),
            new SettingPair("RetryDelay", "600"),
        };
 
    #endregion
    static Technitium.Configuration currentConfig = null;
    Technitium.Configuration CurrentConfig => currentConfig ??= Technitium.Configuration.Load();

    public async Task EnsureSettingsAsync()
    {
        var listenIPs = ListenIPsRaw ?? string.Empty;
        if (listenIPs != CurrentConfig.ListenIPs)
        {
            await SetListenIPs(ListenIPs);
            CurrentConfig.ListenIPs = listenIPs;
            CurrentConfig.Save();
        }
    }

    Technitium.Api api = null;
    public Technitium.Api Api
    {
        get
        {
            if (api == null)
            {
                api = new Technitium.Api(ServerUrl, AdminUser, AdminPassword);
                _ = EnsureSettingsAsync();
            }
            return api;
        }
    }

    #region User

    public async Task<StatusResult> GetStatusAsync()
    {
        RestRequest request = new RestRequest("/api/status", Method.Get);
        return await Api.ExecuteFlatAsync<StatusResult>(request, requireAuth: true);
    }
    #endregion

    #region Settings

    public async Task SetDnsSettingsAsync(object settings)
    {
        RestRequest request = new RestRequest("/api/settings/set", Method.Get);
        Api.AddParams(request, settings);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }

    #endregion

    #region Zones

    public async Task<ZoneInfo[]> ListZonesAsync(string filterName = null, string filterType = null)
    {
        RestRequest request = new RestRequest("/api/zones/list", Method.Get);

        if (!string.IsNullOrEmpty(filterName))
            request.AddQueryParameter("filterName", filterName);

        if (!string.IsNullOrEmpty(filterType))
            request.AddQueryParameter("filterType", filterType);

        ZoneListResult result = await Api.ExecuteAsync<ZoneListResult>(request);
        return result.zones ?? new ZoneInfo[0];
    }

    /// <summary>
    /// Creates a new authoritative zone. Returns the domain name that was actually
    /// created, which can differ from <paramref name="zone"/> for reverse zones.
    /// </summary>
    public async Task<string> CreateZoneAsync(string zone, string type, string[] primaryNameServerAddresses = null)
    {
        RestRequest request = new RestRequest("/api/zones/create", Method.Get);
        request.AddQueryParameter("zone", zone);
        request.AddQueryParameter("type", type);

        if (primaryNameServerAddresses != null && primaryNameServerAddresses.Length > 0)
            request.AddQueryParameter("primaryNameServerAddresses", string.Join(",", primaryNameServerAddresses));

        CreateZoneResult result = await Api.ExecuteAsync<CreateZoneResult>(request);
        return result.domain;
    }

    public async Task DeleteZoneAsync(string zone)
    {
        RestRequest request = new RestRequest("/api/zones/delete", Method.Get);
        request.AddQueryParameter("zone", zone);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }

    public async Task SetZoneOptionsAsync(string zone, IDictionary<string, string> options)
    {
        RestRequest request = new RestRequest("/api/zones/options/set", Method.Get);
        request.AddQueryParameter("zone", zone);
        Api.AddParams(request, options);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }
    public async Task SetZoneOptionsAsync(string zone, object options)
    {
        RestRequest request = new RestRequest("/api/zones/options/set", Method.Get);
        request.AddQueryParameter("zone", zone);
        Api.AddParams(request, options);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }

    #endregion

    #region Records

    public async Task<DnsRecordEntry[]> GetRecordsAsync(string domain, string zone = null, bool listZone = false)
    {
        RestRequest request = new RestRequest("/api/zones/records/get", Method.Get);
        request.AddQueryParameter("domain", domain);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        if (listZone)
            request.AddQueryParameter("listZone", "true");

        RecordsResult result = await Api.ExecuteAsync<RecordsResult>(request);
        return result.records ?? new DnsRecordEntry[0];
    }

    public async Task<DnsRecordEntry> AddRecordAsync(string domain, string type, IDictionary<string, string> parameters, string zone = null, int? ttl = null, bool overwrite = false)
    {
        RestRequest request = new RestRequest("/api/zones/records/add", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        if (ttl.HasValue)
            request.AddQueryParameter("ttl", ttl.Value.ToString());

        if (overwrite)
            request.AddQueryParameter("overwrite", "true");

        Api.AddParams(request, parameters);

        AddRecordResult result = await Api.ExecuteAsync<AddRecordResult>(request);
        return result.addedRecord;
    }
    public async Task<DnsRecordEntry> AddRecordAsync(string domain, string type, object parameters, string zone = null, int? ttl = null, bool overwrite = false)
    {
        RestRequest request = new RestRequest("/api/zones/records/add", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        if (ttl.HasValue)
            request.AddQueryParameter("ttl", ttl.Value.ToString());

        if (overwrite)
            request.AddQueryParameter("overwrite", "true");

        Api.AddParams(request, parameters);

        AddRecordResult result = await Api.ExecuteAsync<AddRecordResult>(request);
        return result.addedRecord;
    }

    public async Task<DnsRecordEntry> UpdateRecordAsync(string domain, string type, IDictionary<string, string> parameters, string zone = null)
    {
        RestRequest request = new RestRequest("/api/zones/records/update", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        Api.AddParams(request, parameters);

        UpdateRecordResult result = await Api.ExecuteAsync<UpdateRecordResult>(request);
        return result.updatedRecord;
    }
    public async Task<DnsRecordEntry> UpdateRecordAsync(string domain, string type, object parameters, string zone = null)
    {
        RestRequest request = new RestRequest("/api/zones/records/update", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        Api.AddParams(request, parameters);

        UpdateRecordResult result = await Api.ExecuteAsync<UpdateRecordResult>(request);
        return result.updatedRecord;
    }

    public async Task DeleteRecordAsync(string domain, string type, IDictionary<string, string> parameters, string zone = null)
    {
        RestRequest request = new RestRequest("/api/zones/records/delete", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        Api.AddParams(request, parameters);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }
    public async Task DeleteRecordAsync(string domain, string type, object parameters, string zone = null)
    {
        RestRequest request = new RestRequest("/api/zones/records/delete", Method.Get);
        request.AddQueryParameter("domain", domain);
        request.AddQueryParameter("type", type);

        if (!string.IsNullOrEmpty(zone))
            request.AddQueryParameter("zone", zone);

        Api.AddParams(request, parameters);

        await Api.ExecuteFlatAsync<ApiResult>(request);
    }

    #endregion

    #region IDnsServerAsync Members

    /// <summary>
    /// Sets the network interface IP addresses (and standard DNS port 53) the DNS
    /// server listens on for DNS protocol requests (Technitium's
    /// "dnsServerLocalEndPoints" setting). IPv6 addresses are bracketed as Technitium
    /// expects (e.g. "[::]:53").
    /// </summary>
    public async Task<bool> SetListenIPs(IEnumerable<IPAddress> listenIPs)
    {
        var ips = listenIPs?.ToArray() ?? Array.Empty<IPAddress>();
        string endPoints;
        if (ips.Length == 0) endPoints = "0.0.0.0:53,[::]:53";
        else endPoints = String.Join(",", ips.Select(FormatDnsEndPoint));

        await SetDnsSettingsAsync(new Technitium.DnsServerLocalEndPointsParameters
        {
            dnsServerLocalEndPoints = endPoints,
        });

        return true;
    }

    static string FormatDnsEndPoint(IPAddress address)
    {
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address + "]"
            : address.ToString();
        return host + ":53";
    }
    public async Task<bool> ZoneExists(string zoneName)
    {
        Technitium.ZoneInfo[] zones = await ListZonesAsync(filterName: zoneName);
        return zones.Any(z => zoneName.Equals(z.name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string[]> GetZones()
    {
        // Technitium always carries its own built-in zones (root hints, reverse
        // lookup zones, "localhost", etc.) marked "internal" - those aren't zones
        // the panel created or should manage, so they're excluded here.
        Technitium.ZoneInfo[] zones = await ListZonesAsync();
        return zones.Where(z => !z.Internal).Select(z => z.name).ToArray();
    }

    /// <summary>
    /// Creates the zone as Primary. <paramref name="secondaryServers"/> are the IP
    /// addresses of the secondary DNS servers that are allowed to transfer (AXFR) this
    /// zone from us - see the caller in DnsServerController.AddZone, which passes the
    /// listening IPs of any configured secondary DNS services plus "AllowZoneTransfers".
    /// </summary>
    public async Task AddPrimaryZone(string zoneName, string[] secondaryServers)
    {
        await CreateZoneAsync(zoneName, "Primary");

        Technitium.ZoneTransferOptions options = new Technitium.ZoneTransferOptions();
        if (secondaryServers != null && secondaryServers.Length > 0)
        {
            options.zoneTransfer = "UseSpecifiedNetworkACL";
            options.zoneTransferNetworkACL = String.Join(",", secondaryServers);
        }
        else
        {
            options.zoneTransfer = "AllowOnlyZoneNameServers";
        }

        await SetZoneOptionsAsync(zoneName, options);
    }

    /// <summary>
    /// Creates the zone as Secondary. <paramref name="masterServers"/> are the IP
    /// addresses of the primary DNS server(s) this zone is transferred from.
    /// </summary>
    public async Task AddSecondaryZone(string zoneName, string[] masterServers)
    {
        await CreateZoneAsync(zoneName, "Secondary", masterServers);
    }

    public async Task DeleteZone(string zoneName)
    {
        await DeleteZoneAsync(zoneName);
    }

    public async Task UpdateSoaRecord(string zoneName, string host, string primaryNsServer, string primaryPerson)
    {
        Technitium.DnsRecordEntry[] records = await GetRecordsAsync(zoneName, zoneName);
        Technitium.DnsRecordEntry soa = records.FirstOrDefault(r => r.type == "SOA");

        if (soa == null)
            throw new ArgumentOutOfRangeException(String.Format("No SOA record found for Technitium DNS zone '{0}'.", zoneName));

        // The refresh/retry/expire/minimum settings are always taken from the
        // provider configuration when set; otherwise the zone's existing values
        // are kept as-is.
        int refresh = RefreshInterval > TimeSpan.Zero ? (int)RefreshInterval.TotalSeconds : soa.GetInt32("refresh");
        int retry = RetryDelay > TimeSpan.Zero ? (int)RetryDelay.TotalSeconds : soa.GetInt32("retry");
        int expire = ExpireLimit > TimeSpan.Zero ? (int)ExpireLimit.TotalSeconds : soa.GetInt32("expire");
        int minimum = MinimumTTL > TimeSpan.Zero ? (int)MinimumTTL.TotalSeconds : soa.GetInt32("minimum");

        // The serial must strictly increase for secondaries/resolvers to notice the change.
        int serial = soa.GetInt32("serial") + 1;

        Technitium.SoaRecordParameters parameters = new Technitium.SoaRecordParameters
        {
            primaryNameServer = primaryNsServer,
            responsiblePerson = primaryPerson,
            serial = serial,
            refresh = refresh,
            retry = retry,
            expire = expire,
            minimum = minimum,
        };

        await UpdateRecordAsync(zoneName, "SOA", parameters, zoneName);
    }

    /// <summary>
    /// Returns all zone records except SOA and any DNSSEC bookkeeping records
    /// (RRSIG/DNSKEY/NSEC3/etc.) which the shared <see cref="DnsRecord"/> model has no
    /// way to represent.
    /// </summary>
    public async Task<DnsRecord[]> GetZoneRecords(string zoneName)
    {
        Technitium.DnsRecordEntry[] entries = await GetRecordsAsync(zoneName, zoneName, listZone: true);

        List<DnsRecord> records = new List<DnsRecord>();

        foreach (Technitium.DnsRecordEntry entry in entries)
        {
            DnsRecordType type;
            if (!TryConvertRecordType(entry.type, out type))
                continue;

            DnsRecord record = new DnsRecord();
            record.RecordType = type;
            record.RecordName = CorrectHost(zoneName, entry.name);

            switch (type)
            {
                case DnsRecordType.A:
                case DnsRecordType.AAAA:
                    record.RecordData = entry.GetString("ipAddress");
                    break;

                case DnsRecordType.NS:
                    record.RecordData = entry.GetString("nameServer");
                    break;

                case DnsRecordType.CNAME:
                    record.RecordData = entry.GetString("cname");
                    break;

                case DnsRecordType.MX:
                    record.RecordData = entry.GetString("exchange");
                    record.MxPriority = entry.GetInt32("preference");
                    break;

                case DnsRecordType.TXT:
                    record.RecordData = entry.GetString("text");
                    break;

                case DnsRecordType.SRV:
                    record.RecordData = entry.GetString("target");
                    record.SrvPriority = entry.GetInt32("priority");
                    record.SrvWeight = entry.GetInt32("weight");
                    record.SrvPort = entry.GetInt32("port");
                    break;
            }

            records.Add(record);
        }

        return records.ToArray();
    }

    public async Task AddZoneRecord(string zoneName, DnsRecord record)
    {
        try
        {
            string domain = BuildFqdn(zoneName, record.RecordName);
            string type = ConvertDnsRecordTypeToString(record.RecordType);
            object parameters = BuildTypeParameters(record, forDelete: false);

            await AddRecordAsync(domain, type, parameters, zoneName);
        }
        catch (Technitium.TechnitiumApiException ex)
        {
            throw new Technitium.TechnitiumApiException(ex.Status, String.Format("Error adding record '{0}' of type '{1}' in Technitium DNS zone '{2}'", record.RecordName, record.RecordType, zoneName), ex.ServerStackTrace, ex.InnerErrorMessage, ex);
        }
        catch (Exception ex)
        {
            throw new Technitium.TechnitiumApiException("", String.Format("Error adding record '{0}' of type '{1}' in Technitium DNS zone '{2}'", record.RecordName, record.RecordType, zoneName), ex.StackTrace, ex.Message, ex);
        }
    }

    public async Task DeleteZoneRecord(string zoneName, DnsRecord record)
    {
        try
        {
            string domain = BuildFqdn(zoneName, record.RecordName);
            string type = ConvertDnsRecordTypeToString(record.RecordType);
            object parameters = BuildTypeParameters(record, forDelete: true);

            await DeleteRecordAsync(domain, type, parameters, zoneName);
        }
        catch (Technitium.TechnitiumApiException ex)
        {
            throw new Technitium.TechnitiumApiException(ex.Status, String.Format("Error deleting record '{0}' of type '{1}' from Technitium DNS zone '{2}'", record.RecordName, record.RecordType, zoneName), ex.ServerStackTrace, ex.InnerErrorMessage, ex);
        }
        catch (Exception ex)
        {
            throw new Technitium.TechnitiumApiException("", String.Format("Error deleting record '{0}' of type '{1}' from Technitium DNS zone '{2}'", record.RecordName, record.RecordType, zoneName), ex.StackTrace, ex.Message, ex);
        }
    }

    public async Task AddZoneRecords(string zoneName, DnsRecord[] records)
    {
        await Task.WhenAll(records.Select(record => AddZoneRecord(zoneName, record)));
    }

    public async Task DeleteZoneRecords(string zoneName, DnsRecord[] records)
    {
        await Task.WhenAll(records.Select(record => DeleteZoneRecord(zoneName, record)));
    }
    #endregion

    #region Zone Record Helpers
    /// <summary>
    /// Builds the type specific request parameters for the Add/Delete Record API calls.
    /// CNAME has no identifying value in the Delete Record call - a zone can only ever
    /// have one CNAME per owner name - so "cname" is left unset (and thus omitted) when
    /// deleting.
    /// </summary>
   object BuildTypeParameters(DnsRecord record, bool forDelete)
    {
        switch (record.RecordType)
        {
            case DnsRecordType.A:
            case DnsRecordType.AAAA:
                return new Technitium.IpAddressRecordParameters { ipAddress = record.RecordData };

            case DnsRecordType.NS:
                return new Technitium.NameServerRecordParameters { nameServer = record.RecordData };

            case DnsRecordType.CNAME:
                return new Technitium.CnameRecordParameters { cname = forDelete ? null : record.RecordData };

            case DnsRecordType.MX:
                return new Technitium.MxRecordParameters { exchange = record.RecordData, preference = record.MxPriority };

            case DnsRecordType.TXT:
                return new Technitium.TxtRecordParameters { text = record.RecordData };

            case DnsRecordType.SRV:
                return new Technitium.SrvRecordParameters
                {
                    priority = record.SrvPriority,
                    weight = record.SrvWeight,
                    port = record.SrvPort,
                    target = record.RecordData,
                };
            default:
                throw new NotSupportedException(String.Format("Record type '{0}' is not supported.", record.RecordType));
        }
    }

    static string ConvertDnsRecordTypeToString(DnsRecordType recordType)
    {
        switch (recordType)
        {
            case DnsRecordType.A: return "A";
            case DnsRecordType.AAAA: return "AAAA";
            case DnsRecordType.NS: return "NS";
            case DnsRecordType.CNAME: return "CNAME";
            case DnsRecordType.MX: return "MX";
            case DnsRecordType.TXT: return "TXT";
            case DnsRecordType.SRV: return "SRV";
            default:
                throw new NotSupportedException(String.Format("Record type '{0}' is not supported.", recordType));
        }
    }

    static bool TryConvertRecordType(string typeName, out DnsRecordType recordType)
    {
        switch (typeName)
        {
            case "A": recordType = DnsRecordType.A; return true;
            case "AAAA": recordType = DnsRecordType.AAAA; return true;
            case "NS": recordType = DnsRecordType.NS; return true;
            case "CNAME": recordType = DnsRecordType.CNAME; return true;
            case "MX": recordType = DnsRecordType.MX; return true;
            case "TXT": recordType = DnsRecordType.TXT; return true;
            case "SRV": recordType = DnsRecordType.SRV; return true;
            default:
                recordType = DnsRecordType.Other;
                return false;
        }
    }

    /// <summary>
    /// Builds the fully qualified domain name Technitium expects as "domain" for a
    /// record, from the zone name and the record's (possibly empty, apex) name.
    /// </summary>
    static string BuildFqdn(string zoneName, string recordName)
    {
        if (String.IsNullOrEmpty(recordName)) return zoneName;

        if (recordName.Equals(zoneName, StringComparison.OrdinalIgnoreCase)
            || recordName.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
            return recordName;

        return recordName + "." + zoneName;
    }

    /// <summary>
    /// Strips the zone name suffix from a record's fully qualified name, mirroring
    /// <see cref="BuildFqdn"/>. Returns string.Empty for the zone apex.
    /// </summary>
    static string CorrectHost(string zoneName, string host)
    {
        if (String.IsNullOrEmpty(host))
            return String.Empty;

        if (host.Equals(zoneName, StringComparison.OrdinalIgnoreCase))
            return String.Empty;

        if (host.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase))
            return host.Substring(0, host.Length - zoneName.Length - 1);

        return host;
    }
    #endregion

    #region Service Items

    public override async Task DeleteServiceItemsAsync(ServiceProviderItem[] items)
    {
        await Task.WhenAll(items
            .Where(item => item is DnsZone)
            .Select(item => DeleteZone(item.Name)));
    }

    #endregion

    #region IsInstalled
    public static async Task<bool> IsInstalledAsync()
    {
        var version = await Plugin.Installer.GetInstalledVersionAsync();
        return version != null && version >= new Version(15, 0);
    }
    #endregion
}
