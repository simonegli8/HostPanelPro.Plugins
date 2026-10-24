SET PackageVersion=0.8.0
SET Configuration=Release

dotnet pack Template.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion%.0 ^
    -p:AssemblyVersion=%PackageVersion%

dotnet new install ..\Library\HostPanelPro.PluginTemplate.0.8.0.nupkg