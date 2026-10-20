#!/bin/bash
dotnet build Publish/HostPanelPro.Plugins.slnx -p:PackToTool=true -p:Configuration=Release -v:minimal
dotnet pack Publish/HostPanelPro.Plugins.slnx -p:PackToTool=true -p:Configuration=Release

dotnet tool uninstall -g HostPanelPro.Plugins.Maker
dotnet tool install -g HostPanelPro.Plugins.Maker --source https://api.nuget.org/v3/index.json --source Library

make-hpp-plugin Plugins/* www