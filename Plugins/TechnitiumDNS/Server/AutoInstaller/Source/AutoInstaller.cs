using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using HostPanelPro.Providers.OS;

namespace HostPanelPro.Plugins.TechnitiumDNS
{
    public class AutoInstaller: IAutoInstaller
    // Discovered by Rhyous.SimplePluginLoader via this attribute.
    // Implement whichever HostPanelPro provider/plugin interface the host expects.
    public class AutoInstaller: IAutoInstaller
    {
        public async Task<bool> IsPluginRequiredAsync() => await Installer.IsInstalledAsync();
    }
}
