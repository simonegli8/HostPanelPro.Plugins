# HostPanelPro Plugin Template

`HostPanelPro.PluginTemplate` is a `dotnet new` template for scaffolding new HostPanelPro
plugins.

## Install the template

```
dotnet new install ./Plugins/HostPanelPro.PluginTemplate
```

## Create a new plugin

```
dotnet new hpp-plugin -n HostPanelPro.Plugins.MyProvider -o Plugins/HostPanelPro.Plugins.MyProvider
```

This produces `Plugins/HostPanelPro.Plugins.MyProvider/HostPanelPro.Plugins.MyProvider.csproj`
with a `MyProviderPlugin` class ready to be filled in.

## Uninstall the template

```
dotnet new uninstall ./Plugins/HostPanelPro.PluginTemplate
```
