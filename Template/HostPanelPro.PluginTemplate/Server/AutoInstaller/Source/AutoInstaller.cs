
using System.Threading.Tasks;

namespace HostPanelPro.Plugins.PluginName1;

public class AutoInstaller : IAutoInstaller
{
    const string PluginName1 = nameof(PluginName1);
    public string PluginId => PluginName1;

    public async Task<bool> IsPluginRequiredAsync()
    {
        // Implement the logic to check if the plugin needs to be installed automatically in the Server
        // component.
        return false;
    }
}
