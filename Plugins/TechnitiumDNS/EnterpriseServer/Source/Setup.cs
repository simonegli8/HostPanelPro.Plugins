using HostPanelPro.EnterpriseServer;
using HostPanelPro.EnterpriseServer.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace HostPanelPro.Plugins.TechnitiumDNS;

public class Setup : IPluginInstaller
{
    public async Task InstallPluginAsync()
    {
        // Add the provider to the database if it doesn't exist
        using var db = new DataProvider();
        if (!db.Providers.Any(p => p.ProviderName == "TechnitiumDNS"))
        {
            var provider = new EnterpriseServer.Data.Entities.Provider()
            {
                DisplayName = "Technitium DNS Server 15.x +",
                EditorControl = "TechnitiumDNS",
                GroupId = 7,
                ProviderName = "TechnitiumDNS",
                ProviderType = "HostPanelPro.Providers.DNS.TechnitiumDNS15, HostPanelPro.Providers.DNS.TechnitiumDNS"
            };
            db.Providers.Add(provider);
            db.SaveChanges();
        }
    }

    public async Task UninstallPluginAsync()
    {
        // Remove the provider from the database if it exists
        using var db = new DataProvider();
        var provider = db.Providers.FirstOrDefault(p => p.ProviderName == "TechnitiumDNS");
        if (provider != null)
        {
            db.Providers.Remove(provider);
            db.SaveChanges();
        }
    }
}
