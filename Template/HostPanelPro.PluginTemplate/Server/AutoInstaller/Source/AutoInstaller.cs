
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins.PluginName1;

public class AutoInstaller : IAutoInstaller
{
    public async IAsyncEnumerable<PluginId> IsPluginRequired()
    {
        // Implement the logic to check if the plugin needs to be installed automatically in the Server
        // component.
        yield break;
    }
}
