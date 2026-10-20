using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using HostPanelPro.Plugins;
using HostPanelPro.EnterpriseServer;
using HostPanelPro.EnterpriseServer.Data;
using HostPanelPro.EnterpriseServer.Data.Entities;
using Twilio.Rest.Trunking.V1;

#if NETCOREAPP
using Microsoft.EntityFrameworkCore;
#endif

namespace HostPanelPro.Plugins.TechnitiumDNS.EnterpriseServer
{
    public class Installer : IPluginInstaller
    {
        public async Task InstallPluginAsync()
        {
            // Add Technitium DNS to Providers table in database
            using var db = new DataProvider();
            if (!db.Providers.Any(p => p.ProviderName == "TechnitiumDNS15"))
            {
                db.Providers.Add(new Provider()
                {
                    DisplayName = "Technitium DNS Server 15.x +",
                    EditorControl = "TechnitiumDNS",
                    GroupId = 7,
                    ProviderName = "TechnitiumDNS15",
                    ProviderType = "HostPanelPro.Providers.DNS.TechnitiumDNS15, HostPanelPro.Plugins.TechnitiumDNS.Server"
                });
                db.SaveChanges();
            }
        }

        public async Task<bool> UninstallPluginAsync()
        {
            try
            {
                using var db = new DataProvider();
                db.Providers.Where(p => p.ProviderName == "TechnitiumDNS15").ExecuteDelete();
                return true;
            } catch { }
            return false;
        }
    }
}
