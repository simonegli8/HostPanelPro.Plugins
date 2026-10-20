using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using HostPanelPro.Web.Services;

namespace HostPanelPro.Providers.DNS.Technitium
{
    /// <summary>
    /// Thrown when the Technitium DNS server HTTP API responds with a non-"ok" status,
    /// or when the HTTP call itself fails.
    /// </summary>
    public class TechnitiumApiException : Exception
    {
        public string Status { get; }
        public string ServerStackTrace { get; }
        public string InnerErrorMessage { get; }

        public TechnitiumApiException(string status, string errorMessage, string serverStackTrace, string innerErrorMessage)
            : base(errorMessage)
        {
            Status = status;
            ServerStackTrace = serverStackTrace;
            InnerErrorMessage = innerErrorMessage;
        }
        public TechnitiumApiException(string status, string errorMessage, string serverStackTrace, string innerErrorMessage, Exception innerException)
            : base(errorMessage, innerException)
        {
            Status = status;
            ServerStackTrace = serverStackTrace;
            InnerErrorMessage = innerErrorMessage;
        }

    }

    // Base envelope shared by every API response.
    public class ApiResult
    {
        public string status { get; set; }
        public string errorMessage { get; set; }
        public string stackTrace { get; set; }
        public string innerErrorMessage { get; set; }
    }

    // Envelope for calls that nest their payload under a "response" property
    // (all zone and record calls).
    class ApiResult<T> : ApiResult
    {
        public T response { get; set; }
    }

    // "login" and "createToken" return their payload at the top level, alongside "status",
    // so this extends ApiResult directly instead of being wrapped by it.
    public class LoginResult : ApiResult
    {
        public string token { get; set; }
        public string username { get; set; }
        public string displayName { get; set; }
        public bool isSsoUser { get; set; }
        public bool totpEnabled { get; set; }
    }

    public class StatusResult : ApiResult
    {
        public bool hasDefaultCredentials { get; set; }
        public bool ssoEnabled { get; set; }
        public string server { get; set; }
    }

    public class ZoneInfo
    {
        public string name { get; set; }
        public string type { get; set; }

        [JsonProperty("internal")]
        public bool Internal { get; set; }

        public string dnssecStatus { get; set; }
        public long soaSerial { get; set; }
        public bool disabled { get; set; }
    }

    class ZoneListResult
    {
        public int pageNumber { get; set; }
        public int totalPages { get; set; }
        public int totalZones { get; set; }
        public ZoneInfo[] zones { get; set; }
    }

    class CreateZoneResult
    {
        public string domain { get; set; }
    }

    class ZoneSummary
    {
        public string name { get; set; }
        public string type { get; set; }
        public bool disabled { get; set; }
    }

    /// <summary>
    /// A single DNS resource record as returned by the Technitium API. The record's type
    /// specific fields (ipAddress, cname, nameServer, exchange/preference, SOA fields, etc.)
    /// live in <see cref="rData"/> since their shape depends on <see cref="type"/>; use
    /// <see cref="GetString"/>/<see cref="GetInt32"/> to pull them out.
    /// </summary>
    public class DnsRecordEntry
    {
        public bool disabled { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public int ttl { get; set; }
        public JObject rData { get; set; }
        public string comments { get; set; }

        public string GetString(string key)
        {
            return (string)rData?[key];
        }

        public int GetInt32(string key)
        {
            JToken value = rData?[key];
            return value == null ? 0 : value.Value<int>();
        }
    }

    class RecordsResult
    {
        public ZoneSummary zone { get; set; }
        public DnsRecordEntry[] records { get; set; }
    }

    class AddRecordResult
    {
        public ZoneSummary zone { get; set; }
        public DnsRecordEntry addedRecord { get; set; }
    }

    class UpdateRecordResult
    {
        public ZoneSummary zone { get; set; }
        public DnsRecordEntry updatedRecord { get; set; }
    }

    // ─── Request parameter DTOs ────────────────────────────────────────────
    // Passed to Api's object-typed SetZoneOptionsAsync/AddRecordAsync/UpdateRecordAsync/
    // DeleteRecordAsync overloads, which reflect over public properties and add each
    // non-null value as a query parameter named after the property - so property names
    // here must match the Technitium API's parameter names exactly (camelCase).

    class ZoneTransferOptions
    {
        public string zoneTransfer { get; set; }
        public string zoneTransferNetworkACL { get; set; }
    }

    class IpAddressRecordParameters
    {
        public string ipAddress { get; set; }
    }

    class NameServerRecordParameters
    {
        public string nameServer { get; set; }
    }

    // cname is left unset (null, so omitted) when building parameters for a delete call -
    // a zone can only ever have one CNAME per owner name, so the value isn't needed there.
    class CnameRecordParameters
    {
        public string cname { get; set; }
    }

    class MxRecordParameters
    {
        public string exchange { get; set; }
        public int preference { get; set; }
    }

    class TxtRecordParameters
    {
        public string text { get; set; }
    }

    class SrvRecordParameters
    {
        public int priority { get; set; }
        public int weight { get; set; }
        public int port { get; set; }
        public string target { get; set; }
    }

    // Passed to Api.SetDnsSettingsAsync (POST/GET api/settings/set). Only the fields this
    // provider actually manages are modeled - unlike SetDomainSettings-style calls, the
    // real endpoint only overwrites the parameters it's given, so omitting the rest here
    // is safe and doesn't clobber any other DNS server setting.
    class DnsServerLocalEndPointsParameters
    {
        public string dnsServerLocalEndPoints { get; set; }
    }

    class SoaRecordParameters
    {
        public string primaryNameServer { get; set; }
        public string responsiblePerson { get; set; }
        public int serial { get; set; }
        public int refresh { get; set; }
        public int retry { get; set; }
        public int expire { get; set; }
        public int minimum { get; set; }
        public bool useSerialDateScheme { get; set; }
    }

    public class Configuration
    {
        public string ListenIPs { get; set; }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(this);
            File.WriteAllText(HostPanelPro.Web.Services.Server.MapPath("~/App_Data/TechnitiumDNS.config.json"), json);
        }

        public static Configuration Load()
        {
            var path = HostPanelPro.Web.Services.Server.MapPath("~/App_Data/TechnitiumDNS.config.json");
            if (!File.Exists(path))
            {
                return new Configuration() { ListenIPs = string.Empty };
            }
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Configuration>(json);
        }

        public override bool Equals(object obj)
        {
            if (obj is Configuration other)
            {
                return this.ListenIPs == other.ListenIPs;
            }
            return false;
        }
        public override int GetHashCode() => ListenIPs.GetHashCode();
    }
}
