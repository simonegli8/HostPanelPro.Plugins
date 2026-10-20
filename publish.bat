dotnet pack Publish\HostPanelPro.Plugins.slnx -p:PackToTool=true

dotnet tool uninstall -g HostPanelPro.Plugins.Maker
dotnet tool install -g HostPanelPro.Plugins.Maker --source https://api.nuget.org/v3/index.json --source Library

make-hpp-plugin Plugins\* www