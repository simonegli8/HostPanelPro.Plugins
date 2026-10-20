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
    {
        public async Task<bool> IsPluginRequiredAsync() => await Installer.IsInstalledAsync();
    }
}
