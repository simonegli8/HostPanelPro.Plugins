using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using HostPanelPro.Providers.OS;

namespace HostPanelPro.Plugins.TechnitiumDNS;

public class Installer
{
    #region IsInstalled
    public static Version GetVersionFromFile(string dll)
    {
        var info = FileVersionInfo.GetVersionInfo(dll);
        if (info.CompanyName != null && info.CompanyName.Contains("Technitium"))
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        else return default;
    }

    public static async Task<Version> GetInstalledVersionAsync()
    {
        const string app = "DnsServerApp";
        if (OSInfo.IsWindows)
        {
            var processes = Process.GetProcessesByName(app)
                .Select(p => p.ExecutableFile())
                .Concat(new string[] { Shell.Default.Find(app) })
                .Distinct()
                .Where(exe => exe != null && File.Exists(exe));
            foreach (var exe in processes)
            {
                var dll = Path.ChangeExtension(exe, ".dll");
                if (!File.Exists(dll)) continue;
                try
                {
                    return GetVersionFromFile(dll);
                }
                catch { }
            }
            return default;
        }
        else
        {
            var psout = await Shell.Standard.ExecAsync("ps aux").Output();
            var match = Regex.Match(psout, @$"[^ ]*/{app}(\.exe|\.dll|(?=\s)|$)?",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match == null || !match.Success) return default;
            var file = match.Value;
            string versionOut = null;
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) file = file + ".dll";
            try
            {
                return GetVersionFromFile(file);
            }
            catch { }
            return default;
        }
    }

    public static async Task<bool> IsInstalledAsync()
    {
        var version = await GetInstalledVersionAsync();
        return version != null && version >= new Version(15, 0);
    }
    #endregion

}
