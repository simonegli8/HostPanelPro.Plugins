cd ..\..\Publish
dotnet build
dotnet tool uninstall -g HostPanelPro.Plugins.Maker
dotnet tool install -g HostPanelPro.Plugins.Maker --source https://api.nuget.org/v3/index.json --source ..\Library

cd ..\Plugins\TechnitiumDNS
make-hpp-plugin . ..\..\www