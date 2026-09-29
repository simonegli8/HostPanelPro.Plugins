using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Threading.Tasks;

namespace HostPanelPro.Plugins;

public interface IAutoInstaller
{
    public IAsyncEnumerable<PluginId> IsPluginRequired();
}

public interface IPluginInstaller
{
    public Task InstallPluginAsync();
}

public interface IPluginStartup
{
    public Task StartPluginAsync();
    public Task StartPlugin();
}

public abstract class AutoInstallerBase: IAutoInstaller
{
    public abstract IAsyncEnumerable<PluginId> IsPluginRequired();
 }
