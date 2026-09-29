using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace HostPanelPro.Plugins
{
    public class PluginInfo
    {
        [JsonIgnore]
        public string Name { get; set; }
        [JsonIgnore]
        public string Image { get; set; }
        [JsonIgnore]
        public string ReadmeMarkdown { get; set; }
        [JsonIgnore]
        public string ReadmeHtml => Markdig.Markdown.ToHtml(ReadmeMarkdown);
        public string Title { get; set; }
        public string Description { get; set; }
        public string Tags { get; set; }
        public DateTime Published { get; set; }
        public Version Version { get; set; }
        public Version MinimumHostPanelProVersion { get; set; }
        public Version MaximumHostPanelProVersion { get; set; }
        public string StartupAssemblies { get; set; }
        public string SetupAssemblies { get; set; }

        [JsonIgnore]
        public bool IsInstalled { get; set; }
    }
}
