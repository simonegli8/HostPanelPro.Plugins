// Copyright (c) 2015, Outercurve Foundation.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//
// - Redistributions of source code must  retain  the  above copyright notice, this
//   list of conditions and the following disclaimer.
//
// - Redistributions in binary form  must  reproduce the  above  copyright  notice,
//   this list of conditions  and  the  following  disclaimer in  the documentation
//   and/or other materials provided with the distribution.
//
// - Neither  the  name  of  the  Outercurve Foundation  nor   the   names  of  its
//   contributors may be used to endorse or  promote  products  derived  from  this
//   software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,  BUT  NOT  LIMITED TO, THE IMPLIED
// WARRANTIES  OF  MERCHANTABILITY   AND  FITNESS  FOR  A  PARTICULAR  PURPOSE  ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
// ANY DIRECT, INDIRECT, INCIDENTAL,  SPECIAL,  EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO,  PROCUREMENT  OF  SUBSTITUTE  GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)  HOWEVER  CAUSED AND ON
// ANY  THEORY  OF  LIABILITY,  WHETHER  IN  CONTRACT,  STRICT  LIABILITY,  OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE)  ARISING  IN  ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using CoreWCF.Channels;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using Corsinvest.ProxmoxVE.Api.Metadata;
using Corsinvest.ProxmoxVE.Api.Shared;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Node;
using Corsinvest.ProxmoxVE.Api.Shared.Models.Vm;
using EstrellasDeEsperanza.AsyncLock;
using HostPanelPro.Providers;
using HostPanelPro.Providers.OS;
using HostPanelPro.Providers.Utils;
using HostPanelPro.Server.Utils;
using HostPanelPro.Web.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.Configuration;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace HostPanelPro.Providers.Virtualization;

public class Proxmox : HostingServiceProviderBase, IVirtualizationServerProxmoxAsync
{
    #region Constants
    private const string CONFIG_USE_DISKPART_TO_CLEAR_READONLY_FLAG = "HostPanelPro.HyperV.UseDiskPartClearReadOnlyFlag";
    private const string WMI_VIRTUALIZATION_NAMESPACE = @"root\virtualization";
    private const string WMI_CIMV2_NAMESPACE = @"root\cimv2";

    private const int SWITCH_PORTS_NUMBER = 1024;
    private const string LIBRARY_INDEX_FILE_NAME = "index.xml";
    private const string EXTERNAL_NETWORK_ADAPTER_NAME = "External Network Adapter";
    private const string PRIVATE_NETWORK_ADAPTER_NAME = "Private Network Adapter";
    private const string MANAGEMENT_NETWORK_ADAPTER_NAME = "Management Network Adapter";

    private const string KVP_RAM_SUMMARY_KEY = "VM-RAM-Summary";
    private const string KVP_HDD_SUMMARY_KEY = "VM-HDD-Summary";
    private const Int64 Size1G = 0x40000000;
    private const Int64 Size1M = 0x100000;

    #endregion

    #region Provider Settings
    protected string ServerName
    {
        get { return ProviderSettings["ServerName"]; }
    }
    protected string ApiUser
    {
        get { return ProviderSettings["ApiUser"]; }
    }
    protected string ApiPassword
    {
        get { return ProviderSettings["ApiPassword"]; }
    }
    protected bool ValidateCertificate
    {
        get { return ProviderSettings.GetBool("ValidateCertificate"); }
    }
    public int AutomaticStartAction
    {
        get { return ProviderSettings.GetInt("StartAction"); }
    }

    public int AutomaticStartupDelay
    {
        get { return ProviderSettings.GetInt("StartupDelay"); }
    }

    public int AutomaticStopAction
    {
        get { return ProviderSettings.GetInt("StopAction"); }
    }

    public int AutomaticRecoveryAction
    {
        get { return 1 /* restart */; }
    }

    public int CpuReserve
    {
        get { return ProviderSettings.GetInt("CpuReserve"); }
    }

    public int CpuLimit
    {
        get { return ProviderSettings.GetInt("CpuLimit"); }
    }

    public int CpuWeight
    {
        get { return ProviderSettings.GetInt("CpuWeight"); }
    }

    public int ServerPort
    {
        get
        {
            if (int.TryParse(ProviderSettings["ServerPort"] ?? "8006", out int port)) return port;
            return 8006;
        }
    }
    #endregion

    #region Api
    PveClient api = null;
    async Task<PveClient> Api() {
         
        api ??= new PveClient(ServerName, ServerPort);
        if (!await api.LoginAsync(ApiUser, ApiPassword))
        {
            throw new Exception("Could not connect to Proxmox server using specified credentials.");
        }
        return api;
    }
    #endregion

    #region Constructors
    public Proxmox()
    {
    }
    #endregion

    #region Virtual Machines
    public VMId VmId(string vmId)
    {
        var tokens = vmId.Split('/');
        if (tokens.Length != 3)
            throw new ArgumentException("Invalid vmId format. Expected node/type/vmid.");
        string nodeName = tokens[0];
        string type = tokens[1];
        long id = long.Parse(tokens[2]);
        return new VMId()
        {
            Node = nodeName,
            Type = type == "qemu" ? VirtualMachineType.Qemu : VirtualMachineType.Lxc,
            Id = id
        };
    }

    protected string VmConfigFile(string vmid) => HostPanelPro.Web.Services.Server.MapPath($"~/App_Data/vm-{vmid}.json");
    protected VirtualMachine LoadVmConfig(string vmid)
    {
        var file = VmConfigFile(vmid);
        if (File.Exists(file))
        {
            var json = File.ReadAllText(file);
            if (!string.IsNullOrWhiteSpace(json))
            {
                return JsonConvert.DeserializeObject<VirtualMachine>(json);
            }
        }
        return new VirtualMachine();
    }
    protected void SaveVmConfig(VirtualMachine vm)
    {
        var file = VmConfigFile(vm.VirtualMachineId);
        var json = JsonConvert.SerializeObject(vm);
        File.WriteAllText(file, json);
    }
    public async Task<VirtualMachine> GetVirtualMachine(string vmId)
    {
        VirtualMachine result = null;
        var id = new VMId(vmId);
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu) { 
            var qemuVm = node.Qemu[id.Id];
            if (qemuVm != null)
            {
                // Fetch VM status/details
                var status = await qemuVm.Status.Current.GetAsync();
                var config = await qemuVm.Config.GetAsync();
                var bootdisk = config.Boot ??
                    config.Boot.Replace("order=", "").Split(',')
                        .FirstOrDefault()?.Trim();
                var drivesinfos = config.ExtensionData
                    .Where(kvp => kvp.Key.Contains("ide") || kvp.Key.Contains("scsi") || kvp.Key.Contains("sata") || kvp.Key.Contains("virtio"));
                var hddinfos = drivesinfos
                    .Where(kvp => !Regex.IsMatch((kvp.Value as string) ?? "", @"media\s*=\s*cdrom"))
                    .Select(kvp => {
                        var tokens = ((string)kvp.Value).Split(',');
                        return new VirtualHardDiskInfo()
                        {
                            ControllerNumber = int.Parse(Regex.Match(kvp.Key, "[0-9]+").Value),
                            ControllerLocation = 1,
                            Path = tokens
                                .Where(token => Regex.IsMatch(token,  @"file\s*="))
                                .FirstOrDefault() ??
                                tokens.FirstOrDefault(),
                            Name = kvp.Key
                        };
                    });
                var cdrom = drivesinfos
                    .First(kvp => Regex.IsMatch((kvp.Value as string) ?? "", @"media\s*=\s*cdrom")).Key;
                var hascdrom = !string.IsNullOrEmpty(cdrom);
                var bootfromcd = bootdisk == cdrom;
                var diskpath = hddinfos
                    .Where(hd => hd.Name == bootdisk)
                    .FirstOrDefault();
                char letter = 'C';
                var vmconfig = LoadVmConfig(vmId);
                result = new VirtualMachine()
                {
                    VirtualMachineId = vmId,
                    Name = vmconfig.Name,
                    State = status.Status == "running" ? VirtualMachineState.Running :
                        status.Status == "paused" ? VirtualMachineState.Paused :
                        status.Status == "stopped" ? VirtualMachineState.Off : VirtualMachineState.Unknown,
                    CpuCores = config.Cores,
                    RamSize = (int)(config.Memory / Size1M), // in MB
                    HddSize = (int)(status.DiskSize / Size1G),
                    //CreatedDate = DateTimeOffset.FromUnixTimeSeconds(config.CreationTime).DateTime,
                    Heartbeat = status.Status == "running" ? OperationalStatus.Ok : OperationalStatus.NoContact,
                    CpuUsage = (int)(status.CpuUsagePercentage + 0.5),
                    RamUsage = (int)(status.MemoryUsage / Size1M), // bytes to MB
                    VirtualHardDrivePath = diskpath?.Path,
                    HddLogicalDisks = config.Disks
                        .Select(disk => new LogicalDisk() {
                            DriveLetter = $"{letter++}:",
                            Size = (int)(long.Parse(disk.Size) / Size1M),
                            FreeSpace = (int)(long.Parse(disk.Size) / Size1M)
                        })
                        .ToArray(),
                    Adapters = config.Networks
                        .Select(net => new VirtualMachineNetworkAdapter() {
                            MacAddress = net.MacAddress,
                            Name = net.Name,
                            SwitchName = ""
                        })
                        .ToArray(),
                    DvdDriveInstalled = hascdrom,
                    Disks = config.Disks
                        .Select(disk =>
                        {
                            VirtualHardDiskFormat format;
                            if (!Enum.TryParse<VirtualHardDiskFormat>(Path.GetExtension(disk.FileName), out format)) format = VirtualHardDiskFormat.QCow2;
                            ControllerType type;
                            if (!Enum.TryParse<ControllerType>(Regex.Replace(disk.Device, "[0-9]+$", ""), out type)) type = ControllerType.SATA;

                            return new VirtualHardDiskInfo()
                            {
                                Attached = true,
                                ControllerLocation = 1,
                                ControllerNumber = 1,
                                DiskFormat = format,
                                DiskType = VirtualHardDiskType.Dynamic,
                                FileSize = long.Parse(disk.Size),
                                Path = disk.FileName,
                                Name = Path.GetFileNameWithoutExtension(disk.FileName),
                                VHDControllerType = type
                            };
                        })
                        .ToArray(),
                    BootFromCD = bootfromcd,
                    DynamicMemory = new DynamicMemory()
                    {
                         Enabled = config.Balloon > 0,
                         Maximum = (int)(config.Memory/Size1M) + (config.Balloon ?? 0),
                         Minimum = (int)(config.Memory/Size1M),
                         Priority = 1024,
                         Buffer = 0
                    },
                    Hostname = config.Name,
                    LegacyNetworkAdapter = config.Networks.Any(net => net.Model != "virtio"),
                    NumLockEnabled = false,
                    Generation = 1,
                    ProcessorCount = 1,
                    VirtualMachineType = VirtualMachineType.Qemu,
                    Uptime = status.Uptime,
                    CreatedDate = vmconfig.CreatedDate,
                    PauseResumeAllowed = vmconfig.PauseResumeAllowed,
                    GroupName = vmconfig.GroupName,
                    Domain = vmconfig.Domain,
                    OperatingSystemTemplate = vmconfig.OperatingSystemTemplate,
                    OperatingSystemTemplatePath = vmconfig.OperatingSystemTemplatePath,
                    Properties = vmconfig.Properties,
                    RebootAllowed = vmconfig.RebootAllowed,
                    ReinstallAllowed = vmconfig.ReinstallAllowed,
                    RemoteDesktopEnabled = vmconfig.RemoteDesktopEnabled,
                    ResetAllowed = vmconfig.ResetAllowed,
                    StartTurnOffAllowed = vmconfig.StartTurnOffAllowed,
                    AdministratorPassword = vmconfig.AdministratorPassword,
                    RootFolderPath = vmconfig.RootFolderPath,
                };
            }
            else return null;
        } else if (id.Type == VirtualMachineType.Lxc) {
            var lxcVm = node.Lxc[id.Id];
            if (lxcVm != null)
            {
                var status = await lxcVm.Status.Current.GetAsync();
                var config = await lxcVm.Config.GetAsync();
                //var drivesinfos = config.ExtensionData
                //    .Where(kvp => Regex.IsMatch(kvp.Key, @"mp[0-9]+") || kvp.Key == "rootfs");
                char letter = 'C';
                var vmconfig = LoadVmConfig(vmId);
                return new VirtualMachine()
                {
                    VirtualMachineId = vmId,
                    Name = vmconfig.Name,
                    State = status.Status == "running" ? VirtualMachineState.Running :
                        status.Status == "paused" ? VirtualMachineState.Paused :
                        status.Status == "stopped" ? VirtualMachineState.Off : VirtualMachineState.Unknown,
                    CpuCores = config.Cores,
                    RamSize = (int)(config.Memory / Size1M), // in MB
                    HddSize = (int)(status.DiskSize / Size1G), // bytes to GB
                    Heartbeat = status.Status == "running" ? OperationalStatus.Ok : OperationalStatus.NoContact,
                    CpuUsage = (int)(status.CpuUsagePercentage + 0.5),
                    RamUsage = (int)(status.MemoryUsage / Size1M), // bytes to MB
                    HddLogicalDisks = config.Disks
                        .Select(disk => new LogicalDisk()
                        {
                            DriveLetter = $"{letter++}:",
                            Size = (int)(long.Parse(disk.Size) / Size1M),
                            FreeSpace = (int)(long.Parse(disk.Size) / Size1M)
                        })
                        .ToArray(),
                    Adapters = config.Networks
                        .Select(net => new VirtualMachineNetworkAdapter()
                        {
                            MacAddress = net.MacAddress,
                            Name = net.Name,
                            SwitchName = ""
                        })
                        .ToArray(),
                    DvdDriveInstalled = false,
                    Uptime = status.Uptime,
                    CreatedDate = vmconfig.CreatedDate,
                    PauseResumeAllowed = vmconfig.PauseResumeAllowed,
                    GroupName = vmconfig.GroupName,
                    Domain = vmconfig.Domain,
                    OperatingSystemTemplate = vmconfig.OperatingSystemTemplate,
                    OperatingSystemTemplatePath = vmconfig.OperatingSystemTemplatePath,
                    Properties = vmconfig.Properties,
                    RebootAllowed = vmconfig.RebootAllowed,
                    ReinstallAllowed = vmconfig.ReinstallAllowed,
                    RemoteDesktopEnabled = vmconfig.RemoteDesktopEnabled,
                    ResetAllowed = vmconfig.ResetAllowed,
                    StartTurnOffAllowed = vmconfig.StartTurnOffAllowed,
                    RootFolderPath = vmconfig.RootFolderPath,
                    AdministratorPassword = vmconfig.AdministratorPassword,
                    BootFromCD = false,
                    Hostname = config.Hostname,
                    Generation = 1,
                    LegacyNetworkAdapter = config.Networks
                        .Any(net => net.Model != "virtio"),
                    ProcessorCount = 1,
                    VirtualMachineType = VirtualMachineType.Lxc,
                };
            }
        }
        return null;
    }

    //public VirtualMachine GetVirtualMachine(string vmId) => GetVirtualMachine(vmId).Result;
    //public VirtualMachine GetVirtualMachineEx(string vmId) => GetVirtualMachine(vmId).Result;

    public async IAsyncEnumerable<VirtualMachine> GetVirtualMachines()
    {
        var queue = Channel.CreateUnbounded<VirtualMachine>();

        var task = Task.Run(async () =>
        {
            var api = await Api();
            var tasks = (await api.Nodes.GetAsync())
                .Select(async node =>
                {
                    var nodeInfo = api.Nodes[node.Id];
                    var qemutask = Task.Run(async () =>
                    {
                        return Task.WhenAll((await nodeInfo.Qemu.GetAsync())
                           .Select(async qemuVm =>
                           {
                               var vm = await GetVirtualMachine($"{node.Id}/qemu/{qemuVm.VmId}");
                               if (vm != null) await queue.Writer.WriteAsync(vm);
                           }));
                    });
                    var lxctask = Task.Run(async () =>
                    {
                        return Task.WhenAll((await nodeInfo.Lxc.GetAsync())
                            .Select(async lxcVm =>
                            {
                                var vm = await GetVirtualMachine($"{node.Id}/lxc/{lxcVm.VmId}");
                                if (vm != null) await queue.Writer.WriteAsync(vm);
                            }));
                    });
                    return Task.WhenAll(qemutask, lxctask);
                });
            await Task.WhenAll(tasks);

            queue.Writer.Complete();
        });

        await foreach (var vm in queue.Reader.ReadAllAsync())
        {
            yield return vm;
        }
    }

    //public List<VirtualMachine> GetVirtualMachines() => GetVirtualMachines().ToListAsync().Result;

    private async Task<PpmImage> GetScreenshot(string vmId)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Qemu)
        {
            var remoteTmpFile = $"/tmp/screendump-{vmId.Replace('/', '-')}-{DateTime.Now.Ticks}.ppm";
            var result = await (await Api()).Nodes[id.Node]?.Qemu[id.Id]?.Monitor.Monitor($"screendump {remoteTmpFile}");
            PpmImage img = null;
            using (var file = new FileStream(remoteTmpFile, FileMode.Open, FileAccess.Read))
            {
                img = PpmImage.FromStream(file);
            }
            File.Delete(remoteTmpFile);
            return img;
        }
        else return null;
    }

    public async Task<ImageFile> GetVirtualMachineThumbnailImage(string vmId, ThumbnailSize size)
    {
        int width = 80;
        int height = 60;

        if (size == ThumbnailSize.Medium160x120)
        {
            width = 160;
            height = 120;
        }
        else if (size == ThumbnailSize.Large320x240)
        {
            width = 320;
            height = 240;
        }

        return await GetVirtualMachineThumbnailImage(vmId, width, height);
    }

    public SKImage ResizeImage(SKImage img, int width, int height)
    {
        SKImageInfo thumbinfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        SKImage thumb = SKImage.Create(thumbinfo);
        img.ScalePixels(thumb.PeekPixels(), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return thumb;
    }
    public ImageFile GetOffImage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var svgName = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith("power-button.svg", StringComparison.OrdinalIgnoreCase));
        using (var res = assembly.GetManifestResourceStream(svgName))
        {
            var bytes = new byte[res.Length];
            res.ReadExactly(bytes);
            return new ImageFile()
            {
                MimeType = "image/svg+xml",
                FileExtension = "svg",
                RawData = bytes
            };
        }
    }
    public ImageFile GetImageFile(SKImage img)
    {
        MemoryStream stream = new MemoryStream();
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        {
            data.SaveTo(stream);
        }
        stream.Flush();
        var buffer = stream.ToArray();

        var imageFile = new ImageFile()
        {
            FileExtension = "png",
            MimeType = "image/png",
            RawData = buffer
        };
        return imageFile;

    }
    public async Task<ImageFile> GetVirtualMachineThumbnailImage(string vmId, int width, int height)
    {
        SKImage image = null;
        try
        {
            image = (await GetScreenshot(vmId)).SkiaImage;
        }
        catch
        {
            return GetOffImage();
        }

        var thumb = ResizeImage(image, width, height);

        return GetImageFile(thumb);
    }

    private async IAsyncEnumerable<long> GetQemuIds(string node)
    {
        var qemuVminfos = await (await Api()).Nodes[node].Qemu.GetAsync();
        foreach (var info in qemuVminfos)
            yield return info.VmId;
    }
    private async IAsyncEnumerable<long> GetLxcIds(string node)
    {
        var lxcVminfos = await (await Api()).Nodes[node].Lxc.GetAsync();
        foreach (var info in lxcVminfos)
            yield return info.VmId;
    }

    static AsyncLock IdLock = new AsyncLock();
    public async Task<VirtualMachine> CreateVirtualMachine(VirtualMachine vm)
    {
        vm.RootFolderPath = FileUtils.EvaluateSystemVariables(vm.RootFolderPath);
        vm.OperatingSystemTemplatePath = FileUtils.EvaluateSystemVariables(vm.OperatingSystemTemplatePath);
        vm.VirtualHardDrivePath = FileUtils.EvaluateSystemVariables(vm.VirtualHardDrivePath);

        if (vm.VirtualMachineType == VirtualMachineType.Unknown) vm.VirtualMachineType = VirtualMachineType.Qemu;
        if (vm.VirtualMachineType == VirtualMachineType.HyperV) throw new ArgumentException("Cannot create Hyper-V virtual machines on Proxmox server.");

        using (var mylock = await IdLock.LockAsync())
        {
            // generate VM id
            VMId id = null;
            var api = await Api();
            if (!string.IsNullOrEmpty(vm.VirtualMachineId)) id = new VMId(vm.VirtualMachineId);
            else
            {
                id.Type = vm.VirtualMachineType;
                id.Node = Environment.MachineName;
                var result = await api.Cluster.Nextid.Nextid();
                if (result.IsSuccessStatusCode)
                {
                    id.Id = long.Parse(result.Response.data.ToString());
                    vm.VirtualMachineId = $"{id.Node}/{(id.Type == VirtualMachineType.Qemu ? "qemu" : "lxc")}/{id.Id}";
                }
                else
                {
                    throw new Exception("Could not get next VM ID from Proxmox server.");
                }
            }

            var node = api.Nodes[id.Node];
            if (!string.IsNullOrEmpty(vm.OperatingSystemTemplate))
            {
                if (Regex.IsMatch(vm.OperatingSystemTemplate, @"^(.*?/(?:qemu|lxc)/)?\*?[0-9]+")) // Template is a VM ID
                {
                    var templateId = long.Parse(Regex.Match(vm.OperatingSystemTemplate, @"(?<=^(.*?/(?:qemu|lxc)/)?\*?)[0 - 9]+").Value);
                    bool linkTemplate = Regex.IsMatch(vm.OperatingSystemTemplate, @"(?<=^(.*?/(?:qemu|lxc)/)?\*)[0 - 9]+");
                    if (vm.VirtualMachineType == VirtualMachineType.Qemu)
                    {
                        var result = node.Qemu[templateId].Clone.CloneVm((int)id.Id, null, vm.Name, null, !linkTemplate, vm.Name, null, null, null, null).Result;
                        if (!result.IsSuccessStatusCode) throw new InvalidOperationException("Failed to clone VM from template VM-{templateId}. " + result.GetError());
                    } else
                    {
                        var result = node.Lxc[templateId].Clone.CloneVm((int)id.Id, null, vm.Name, !linkTemplate, vm.Name, null, null, null, null).Result;
                        if (!result.IsSuccessStatusCode) throw new InvalidOperationException("Failed to clone container from template CT-{templateId}. " + result.GetError());
                    }
                }
                else // Template is a storage:path
                {
                    throw new NotImplementedException("Creating VM from storage template is not implemented.");
                }
            }
        }
        
        await UpdateVirtualMachine(vm);

        return await GetVirtualMachine(vm.VirtualMachineId);
    }
    public async Task<VirtualMachine> UpdateVirtualMachine(VirtualMachine vm)
    {
        string vmId = vm.VirtualMachineId;
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = vm.VirtualMachineType = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        var api = await Api();
        var node = api.Nodes[id.Node];

        var cpus = Environment.ProcessorCount;
        var balloon = vm.DynamicMemory != null && vm.DynamicMemory.Enabled ?
            vm.DynamicMemory.Maximum - vm.DynamicMemory.Minimum :
            0;
        var mem = vm.RamSize;
        var ix = new int[4];
        var disks = vm.Disks
             ?.Where(disk => disk.Attached)
             ?.Select(disk => new
             {
                 Disk = $"file={disk.Path}, size={disk.FileSize}G,format={disk.DiskFormat.ToString().ToLower()}",
                 Type = disk.VHDControllerType,
                 N = ix[(int)disk.VHDControllerType]++
             });
        if (vm.DvdDriveInstalled)
        {
            var idecount = disks.Where(disk => disk.Type == ControllerType.IDE).Count();
            disks = disks.Concat(
                new[] { new {
                    Disk = "none,media=cdrom",
                    Type = idecount >= 4 ? ControllerType.IDE : ControllerType.SATA,
                    N = ix[(int)(idecount >= 4 ? ControllerType.IDE : ControllerType.SATA)]++
                }});
        }
        var sata = disks
            .Where(disk => disk.Type == ControllerType.SATA)
            .ToDictionary(disk => disk.N, disk => disk.Disk);
        var ide = disks
            .Where(disk => disk.Type == ControllerType.IDE)
            .ToDictionary(disk => disk.N, disk => disk.Disk);
        var scsi = disks
            .Where(disk => disk.Type == ControllerType.SCSI)
            .ToDictionary(disk => disk.N, disk => disk.Disk);
        var virtio = disks
            .Where(disk => disk.Type == ControllerType.Virtio)
            .ToDictionary(disk => disk.N, disk => disk.Disk);
        string boot;
        var hdd = disks.FirstOrDefault(disk => !disk.Disk.Contains("media=cdrom"));
        if (vm.BootFromCD)
        {
            var dvd = disks.FirstOrDefault(disk => disk.Disk.Contains("media=cdrom"));
            boot = $"order={dvd.Type.ToString().ToLower()}{dvd.N};{hdd.Type.ToString().ToLower()}{hdd.N}";
        } else
        {
            boot = $"order={hdd.Type.ToString().ToLower()}{hdd.N}";
        }
        int n = 0;
        var netinterfaces = vm.Adapters
            .Select(net => $"model={(vm.LegacyNetworkAdapter ? "e1000" : "virtio")},bridge={net.SwitchName},macaddr={net.MacAddress}")
            .ToDictionary<string, int>(net => n++);
        if (id.Type == VirtualMachineType.Qemu)
        {
            await (await Api()).Nodes[id.Node].Qemu[id.Id].Config.UpdateVmAsync(acpi: true, agent: "1", balloon: balloon, 
                boot: boot, cores: vm.CpuCores, cpulimit: cpus*((float)vm.CpuUsage) / 100, cpuunits: 1024, description: vm.Name, force: true, freeze: false,
                hotplug: "network,disk,usb", ideN: ide, ipconfigN: netinterfaces, kvm: true, localtime: true, 
                memory: vm.RamSize.ToString(), name: vm.Name, netN: netinterfaces, onboot: true, reboot: true, sataN: sata, 
                scsiN: scsi, virtioN: virtio);
        } else
        {
            await (await Api()).Nodes[id.Node].Lxc[id.Id].Config.UpdateVm(cores: vm.CpuCores, cpulimit: cpus*((float)vm.CpuUsage)/100,
                cpuunits: 1024, debug: false, description: vm.Name, hostname: vm.Name, memory: vm.RamSize,
                netN: netinterfaces, onboot: true, protection: true);
        }

        SaveVmConfig(vm);

        return vm;
    }

    public async Task<JobResult> ChangeVirtualMachineState(string vmId, VirtualMachineRequestedState newState)
    {
        var id = new VMId(vmId);
        var node = (await Api()).Nodes[id.Node];
        Result result = null;
        var config = LoadVmConfig(vmId);
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qemuVm = node.Qemu[id.Id];
            if (qemuVm != null)
            {
                switch (newState)
                {
                    case VirtualMachineRequestedState.ShutDown:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await qemuVm.Agent.Shutdown.Shutdown();
                            if (!result.IsSuccessStatusCode) result = await qemuVm.Status.Shutdown.VmShutdown();
                        }
                        break;
                    case VirtualMachineRequestedState.Reboot:
                        if (config.RebootAllowed)
                        {
                            // shutdown
                            result = await qemuVm.Agent.Shutdown.Shutdown();
                            if (!result.IsSuccessStatusCode) result = await qemuVm.Status.Shutdown.VmShutdown();
                            // start
                            if (result.IsSuccessStatusCode) result = await qemuVm.Status.Start.VmStart();
                        }
                        break;
                    case VirtualMachineRequestedState.Start:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await qemuVm.Status.Start.VmStart();
                        }
                        break;
                    case VirtualMachineRequestedState.Pause:
                        if (config.PauseResumeAllowed)
                        {
                            result = await qemuVm.Status.Suspend.VmSuspend();
                        }
                        break;
                    case VirtualMachineRequestedState.Resume:
                        if (config.PauseResumeAllowed)
                        {
                            result = await qemuVm.Status.Resume.VmResume();
                        }
                        break;
                    case VirtualMachineRequestedState.Reset:
                        if (config.ResetAllowed)
                        {
                            result = await qemuVm.Status.Reset.VmReset();
                        }
                        break;
                    case VirtualMachineRequestedState.Save:
                        if (config.PauseResumeAllowed)
                        {
                            result = await qemuVm.Status.Suspend.VmSuspend(null, null, true);
                        }
                        break;
                    case VirtualMachineRequestedState.TurnOff:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await qemuVm.Status.Stop.VmStop();
                        }
                        break;
                }
            }
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var lxcVm = node.Lxc[id.Id];
            if (lxcVm != null)
            {
                switch (newState)
                {
                    case VirtualMachineRequestedState.ShutDown:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await lxcVm.Status.Shutdown.VmShutdown(true);
                        }
                        break;
                    case VirtualMachineRequestedState.TurnOff:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await lxcVm.Status.Stop.VmStop();
                        }
                        break;
                    case VirtualMachineRequestedState.Reset:
                        if (config.ResetAllowed)
                        {
                            result = await lxcVm.Status.Stop.VmStop();
                        }
                        break;
                    case VirtualMachineRequestedState.Reboot:
                        if (config.RebootAllowed)
                        {
                            result = await lxcVm.Status.Shutdown.VmShutdown(true);
                            if (result.IsSuccessStatusCode) result = await lxcVm.Status.Start.VmStart();
                        }
                        break;
                    case VirtualMachineRequestedState.Pause:
                        if (config.PauseResumeAllowed)
                        {
                            result = await lxcVm.Status.Suspend.VmSuspend();
                        }
                        break;
                    case VirtualMachineRequestedState.Resume:
                        if (config.PauseResumeAllowed)
                        {
                            result = await lxcVm.Status.Resume.VmResume();
                        }
                        break;
                    case VirtualMachineRequestedState.Start:
                        if (config.StartTurnOffAllowed)
                        {
                            result = await lxcVm.Status.Start.VmStart();
                        }
                        break;
                    case VirtualMachineRequestedState.Save:
                        throw new NotImplementedException();
                }
            }
        }
        else throw new NotSupportedException($"Wrong VirtualMachine type {id.Type}.");

        return await ToJobResult(result);
    }

    public async Task<Result> ShutDownVirtualMachine(string vmId, bool force, string reason)
    {
        var id = new VMId(vmId);
        var node = (await Api()).Nodes[id.Node];
        Result result = null;
        var config = LoadVmConfig(vmId);
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qemuVm = node.Qemu[id.Id];
            if (qemuVm != null)
            {
                if (config.StartTurnOffAllowed)
                {
                    result = await qemuVm.Agent.Shutdown.Shutdown();
                    if (!result.IsSuccessStatusCode) result = await qemuVm.Status.Shutdown.VmShutdown(force);
                }
            }
        } else if (id.Type == VirtualMachineType.Lxc)
        {
            var lxcVm = node.Lxc[id.Id];
            if (lxcVm != null)
            {
                if (config.StartTurnOffAllowed)
                {
                    result = await lxcVm.Status.Shutdown.VmShutdown(force);
                }
            }
        }
        return result;
    }

    public async IAsyncEnumerable<ConcreteJob> GetVirtualMachineJobs(string vmId)
    {
        var id = new VMId(vmId);

        var result = await (await Api()).Nodes[id.Node].Tasks.NodeTasks(vmid: (int)id.Id);
        if (!result.IsSuccessStatusCode) yield break;

        JArray tasks = result.Response.data;
        foreach (var task in tasks.OfType<JObject>())
        {
            yield return ToConcreteJob(task);
        }
    }

    public async Task<JobResult> RenameVirtualMachine(string vmId, string name)
    {
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        var node = (await Api()).Nodes[id.Node];
        var config = LoadVmConfig(vmId);
        if (config != null) config.Name = name;
        SaveVmConfig(config);

        Result result;
        if (id.Type == VirtualMachineType.Qemu)
        {
            result = await node.Qemu[id.Id].Config.UpdateVmAsync(name: name, description: name);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            result = await node.Lxc[id.Id].Config.UpdateVm(description: name);
        }
        else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return await ToJobResult(result);
    }

    public async Task<JobResult> DeleteVirtualMachine(string vmId)
    {
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        var node = (await Api()).Nodes[id.Node];
        Result result;
        if (id.Type == VirtualMachineType.Qemu)
        {
            result = await node.Qemu[id.Id].DestroyVm(destroy_unreferenced_disks: true, purge: true);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            result = await node.Lxc[id.Id].DestroyVm(destroy_unreferenced_disks: true, purge: true, force: true);
        }
        else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return await ToJobResult(result);
    }

    public async Task<JobResult> ExportVirtualMachine(string vmId, string exportPath)
    {
        throw new NotSupportedException();
    }
    #endregion

    #region Snapshots
    public async IAsyncEnumerable<VirtualMachineSnapshot> GetVirtualMachineSnapshots(string vmId)
    {
        List<VirtualMachineSnapshot> snapshots = new List<VirtualMachineSnapshot>();
        var id = new VMId(vmId);

        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Kvm)
        {
            var vm = node.Qemu[id.Id];
            var result = await vm.Snapshot.GetAsync();
            foreach (var snapshot in result)
            {
                yield return new VirtualMachineSnapshot()
                {
                    Name = snapshot.Name,
                    Created = new DateTime(1970, 1, 1).Add(TimeSpan.FromSeconds(snapshot.Time)),
                    IsCurrent = snapshot.Name == "current",
                    VMName = vmId,
                    ParentId = snapshot.Parent,
                    Id = snapshot.Name
                };
            }
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var vm = node.Lxc[id.Id];
            var result = await vm.Snapshot.GetAsync();
            foreach (var snapshot in result)
            {
                yield return new VirtualMachineSnapshot()
                {
                    Name = snapshot.Name,
                    Created = new DateTime(1970, 1, 1).Add(TimeSpan.FromSeconds(snapshot.Time)),
                    IsCurrent = snapshot.Name == "current",
                    VMName = vmId,
                    ParentId = snapshot.Parent,
                    Id = snapshot.Name
                };
            }
        } else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");
    }

    public async Task<VirtualMachineSnapshot> GetSnapshot(string vmId, string snapshotId)
    {
        var snapshots = GetVirtualMachineSnapshots(vmId);
        return await snapshots.FirstOrDefaultAsync(s => s.Id == snapshotId);
    }
    public async Task<JobResult> CreateSnapshot(string vmId)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        var vm = await GetVirtualMachine(vmId);
        string snapname = $"wsp{vmId.Split(':')[1]}uid{Guid.NewGuid().ToString().GetHashCode().ToString("x")}";
        string desc = $"{vm.Name} - Snapshot - ({DateTime.Now.ToString("yyyy.MM.dddd HH:mm:ss")})";

        var img = await GetVirtualMachineThumbnailImage(vmId, ThumbnailSize.Large320x240);
        if (img.MimeType == "image/png") File.WriteAllBytes(HostPanelPro.Web.Services.Server.MapPath($"~/App_Data/Snapshot.Img.{snapname}.png"), img.RawData);

        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qvm = node.Qemu[id.Id];
            result = await qvm.Snapshot.Snapshot(snapname, desc);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var lxc = node.Lxc[id.Id];
            result = await lxc.Snapshot.Snapshot(snapname, desc);
        } else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return await ToJobResult(result);
    }

    public async Task<JobResult> RenameSnapshot(string vmId, string snapshotId, string name)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        // Proxmox snapshot names are immutable identifiers; only the description (used as the display name) can be changed.
        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            result = await node.Qemu[id.Id].Snapshot[snapshotId].Config.UpdateSnapshotConfig(description: name);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            result = await node.Lxc[id.Id].Snapshot[snapshotId].Config.UpdateSnapshotConfig(description: name);
        }
        else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return await ToJobResult(result);
    }

    public async Task<JobResult> RollbackSnapshot(string vmId, string snapshotId)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qvm = node.Qemu[id.Id];
            result = await qvm.Snapshot[snapshotId].Rollback.Rollback(true);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var lxc = node.Lxc[id.Id];
            result = await lxc.Snapshot[snapshotId].Rollback.Rollback(true);
        } else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return await ToJobResult(result);
    }

    private async Task<Result> DeleteSnapshotCore(string vmId, string snapshotId)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;

        var file = $"~/App_Data/Snapshot.Img.{snapshotId}.png";
        if (File.Exists(file)) File.Delete(file);

        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qvm = node.Qemu[id.Id];
            result = await qvm.Snapshot[snapshotId].Delsnapshot(true);
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var lxc = node.Lxc[id.Id];
            result = await lxc.Snapshot[snapshotId].Delsnapshot(true);
        }
        else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");

        return result;
    }

    public async Task<JobResult> DeleteSnapshot(string vmId, string snapshotId)
    {
        var result = await DeleteSnapshotCore(vmId, snapshotId);
        return await ToJobResult(result);
    }

    private static IEnumerable<VirtualMachineSnapshot> GetDescendantSnapshotsDeepestFirst(IReadOnlyCollection<VirtualMachineSnapshot> all, string snapshotId)
    {
        foreach (var child in all.Where(s => s.ParentId == snapshotId))
        {
            foreach (var grandchild in GetDescendantSnapshotsDeepestFirst(all, child.Id))
                yield return grandchild;
            yield return child;
        }
    }

    public async Task<JobResult> DeleteSnapshotSubtree(string vmId, string snapshotId)
    {
        var snapshots = await GetVirtualMachineSnapshots(vmId).ToListAsync();

        // Proxmox has no "delete subtree" operation, so descendants must be deleted individually,
        // deepest first, waiting for each background task to finish before the parent can be removed.
        foreach (var descendant in GetDescendantSnapshotsDeepestFirst(snapshots, snapshotId))
        {
            var childResult = await DeleteSnapshotCore(vmId, descendant.Id);
            if (childResult != null && childResult.IsSuccessStatusCode)
            {
                string childUpid = childResult.Response.data as string;
                if (!string.IsNullOrEmpty(childUpid) && childUpid.StartsWith("UPID:")) await WaitForJob(childUpid);
            }
        }

        var result = await DeleteSnapshotCore(vmId, snapshotId);
        return await ToJobResult(result);
    }

    public async Task<ImageFile> GetSnapshotThumbnailImage(string snapshotId, ThumbnailSize size)
    {
        var file = $"~/App_Data/Snapshot.Img.{snapshotId}.png";
        if (!File.Exists(file)) return GetOffImage();

        var img = SKImage.FromBitmap(SKBitmap.Decode(file));
 
        int width = 80;
        int height = 60;

        if (size == ThumbnailSize.Medium160x120)
        {
            width = 160;
            height = 120;
        }
        else if (size == ThumbnailSize.Large320x240)
        {
            width = 320;
            height = 240;
        }

        img = ResizeImage(img, width, height);
        return GetImageFile(img);
    }
    #endregion

    #region DVD operations
    public async IAsyncEnumerable<LibraryItem> GetDVDISOs(string vmId)
    {
        var id = new VMId(vmId);
        var api = await Api();

        var storagesResult = await api.Nodes[id.Node].Storage.Index(content: "iso");
        if (!storagesResult.IsSuccessStatusCode) yield break;

        JArray storages = storagesResult.Response.data;
        var isos = new List<LibraryItem>();
        var node = api.Nodes[id.Node];

        var tasks = storages.OfType<JObject>()
            .Select(async storage => new
            {
                Id = (string)storage["storage"],
                Volumes = await node.Storage[(string)storage["storage"]].Content.Index(content: "iso")
            })
            .ToList();

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            var storage = await completed;
            if (!storage.Volumes.IsSuccessStatusCode) continue;

            JArray volumes = storage.Volumes.Response.data;
            foreach (var volume in volumes.OfType<JObject>())
            {
                var volid = (string)volume["volid"];
                if (string.IsNullOrEmpty(volid)) continue;

                var slash = volid.IndexOf('/');
                yield return new LibraryItem()
                {
                    Path = volid,
                    Name = slash >= 0 ? volid.Substring(slash + 1) : volid
                };
            }
        }
    }

    public async Task<string> GetInsertedDVD(string vmId)
    {
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        //Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var vmconfig = await node.Qemu[id.Id].Config.GetAsync(current: true); // VmConfig(current: true);
            if (vmconfig != null)
            {
                return vmconfig.DisksAll
                    .FirstOrDefault(disk => disk.Kind == VmDiskKind.Cdrom && !disk.IsUnused)?.Id;
            }
        }
        else if (id.Type == VirtualMachineType.Lxc)
        {
            var vmconfig = await node.Lxc[id.Id].Config.GetAsync(current: true);
            if (vmconfig != null)
            {
                return vmconfig.DisksAll
                    .FirstOrDefault(disk => disk.Kind == VmDiskKind.Cdrom && !disk.IsUnused)?.Id;
            }
        }
        else throw new NotSupportedException("No QEMU neither LXC virtual machine ID.");
        return null;
    }

    class DriveID
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public int N { get; set; }
    }
    public async Task<JobResult> InsertDVD(string vmId, string isoPath)
    {
        isoPath = FileUtils.EvaluateSystemVariables(isoPath);
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qvm = node.Qemu[id.Id];
            result = await qvm.Config.VmConfig(current: true);
            if (result.IsSuccessStatusCode)
            {
                JObject config = result.Response.data;
                var drives = config
                    .OfType<KeyValuePair<string, JToken>>()
                    .Where(prop => prop.Key.StartsWith("ide") || prop.Key.StartsWith("sata") || prop.Key.StartsWith("scsi") ||
                        prop.Key.StartsWith("virtio"))
                    .Select(p => new DriveID()
                    {
                        Key = p.Key,
                        Value = (string)p.Value,
                        N = int.Parse(Regex.Match(p.Key, "[0-9]+").Value)
                    });
                var drive = drives
                    .Where(prop => prop.Value.Contains("media=cdrom"))
                    .FirstOrDefault();
                var driveid = drive.Key;
                var driveval = $"{isoPath},media=cdrom";
                int driven = 0;
                if (string.IsNullOrEmpty(driveid))
                {
                    drive = drives
                        .Where(prop => prop.Key.StartsWith("sata"))
                        .OrderBy(prop => prop.Key)
                        .LastOrDefault();
                    if (drive.Key == null)
                    {
                        driveid = "sata0";
                        driven = 0;
                    }
                    else
                    {
                        driven = drive.N + 1;
                        driveid = $"sata{driven}";
                    }
                }
                if (drives.Any(p => p.Key == driveid))
                {
                    drives = drives
                        .Select(drive => drive.Key != driveid ?
                            drive : new DriveID()
                            {
                                Key = driveid,
                                Value = driveval,
                                N = driven
                            });
                }
                else
                {
                    drives = drives
                        .Concat(new[] {
                            new DriveID()
                            {
                                Key = driveid,
                                Value = driveval,
                                N = driven
                            }
                        });
                }
                result = await qvm.Config.UpdateVm(
                    ideN: drives.Where(d => d.Key.StartsWith("ide")).ToDictionary(d => d.N, d => d.Value),
                    scsiN: drives.Where(d => d.Key.StartsWith("scsi")).ToDictionary(d => d.N, d => d.Value),
                    sataN: drives.Where(d => d.Key.StartsWith("sata")).ToDictionary(d => d.N, d => d.Value),
                    virtioN: drives.Where(d => d.Key.StartsWith("virtio")).ToDictionary(d => d.N, d => d.Value));
            }
            return await ToJobResult(result);
        }
        throw new NotSupportedException("Cannot insert DVD into LXC container.");
    }

    public async Task<JobResult> EjectDVD(string vmId)
    {
        var id = new VMId(vmId);

        if (id.Type == VirtualMachineType.Unknown) id.Type = VirtualMachineType.Qemu;
        if (id.Type == VirtualMachineType.HyperV) throw new ArgumentException("Cannot update Hyper-V virtual machines on Proxmox server.");

        Result result;
        var node = (await Api()).Nodes[id.Node];
        if (id.Type == VirtualMachineType.Qemu)
        {
            var qvm = node.Qemu[id.Id];
            result = await qvm.Config.VmConfig(current: true);
            if (result.IsSuccessStatusCode)
            {
                JObject config = result.Response.data;
                var drives = config
                    .OfType<KeyValuePair<string, JToken>>()
                    .Where(prop => prop.Key.StartsWith("ide") || prop.Key.StartsWith("sata") || prop.Key.StartsWith("scsi") ||
                        prop.Key.StartsWith("virtio"))
                    .Select(p => new DriveID()
                    {
                        Key = p.Key,
                        Value = (string)p.Value,
                        N = int.Parse(Regex.Match(p.Key, "[0-9]+").Value)
                    });
                var drive = drives
                    .Where(prop => prop.Value.Contains("media=cdrom"))
                    .FirstOrDefault();
                if (drive == null || string.IsNullOrEmpty(drive.Key))
                {
                    // No cdrom drive present, nothing to eject.
                    return await ToJobResult(result);
                }

                drives = drives
                    .Select(d => d.Key != drive.Key ?
                        d : new DriveID()
                        {
                            Key = drive.Key,
                            Value = "none,media=cdrom",
                            N = drive.N
                        });

                result = await qvm.Config.UpdateVm(
                    ideN: drives.Where(d => d.Key.StartsWith("ide")).ToDictionary(d => d.N, d => d.Value),
                    scsiN: drives.Where(d => d.Key.StartsWith("scsi")).ToDictionary(d => d.N, d => d.Value),
                    sataN: drives.Where(d => d.Key.StartsWith("sata")).ToDictionary(d => d.N, d => d.Value),
                    virtioN: drives.Where(d => d.Key.StartsWith("virtio")).ToDictionary(d => d.N, d => d.Value));
            }
            return await ToJobResult(result);
        }
        throw new NotSupportedException("Cannot eject DVD from LXC container.");
    }
    #endregion

    #region Virtual Switches
    private async Task<IEnumerable<JObject>> GetNodeBridges(string node)
    {
        var result = await (await Api()).Nodes[node].Network.Index("bridge");
        if (!result.IsSuccessStatusCode) return Enumerable.Empty<JObject>();

        JArray ifaces = result.Response.data;
        return ifaces.OfType<JObject>();
    }

    private static VirtualSwitch ToVirtualSwitch(JObject iface)
    {
        var name = (string)iface["iface"];
        var ports = (string)iface["bridge_ports"];
        return new VirtualSwitch()
        {
            SwitchId = name,
            Name = name,
            SwitchType = string.IsNullOrWhiteSpace(ports) ? "Private" : "External"
        };
    }

    public async IAsyncEnumerable<VirtualSwitch> GetSwitches()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = await (await Api()).Nodes.GetAsync();
        foreach (var node in nodes)
        {
            foreach (var iface in await GetNodeBridges(node.Id))
            {
                var sw = ToVirtualSwitch(iface);
                if (seen.Add(sw.SwitchId)) yield return sw;
            }
        }
    }

    public async IAsyncEnumerable<VirtualSwitch> GetExternalSwitches(string computerName)
    {
        foreach (var iface in await GetNodeBridges(computerName))
        {
            var ports = (string)iface["bridge_ports"];
            if (string.IsNullOrWhiteSpace(ports)) continue; // not bound to a physical adapter, so not "external"
            yield return ToVirtualSwitch(iface);
        }
    }

    public async Task<bool> SwitchExists(string switchId)
    {
        var nodes = await (await Api()).Nodes.GetAsync();
        foreach (var node in nodes)
        {
            var ifaces = await GetNodeBridges(node.Id);
            if (ifaces.Any(iface => string.Equals((string)iface["iface"], switchId, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public async Task<VirtualSwitch> CreateSwitch(string name)
    {
        var node = (await Api()).Nodes[Environment.MachineName];

        var result = await node.Network.CreateNetwork(iface: name, type: "bridge", autostart: true, bridge_vlan_aware: false);
        if (!result.IsSuccessStatusCode) throw new InvalidOperationException($"Failed to create virtual switch '{name}'. " + result.GetError());

        await node.Network.ReloadNetworkConfig();

        return new VirtualSwitch()
        {
            SwitchId = name,
            Name = name,
            SwitchType = "Private"
        };
    }

    public async Task<ReturnCode> DeleteSwitch(string switchId)
    {
        var node = (await Api()).Nodes[Environment.MachineName];

        var result = await node.Network[switchId].DeleteNetwork();
        if (!result.IsSuccessStatusCode) return ReturnCode.Failed;

        await node.Network.ReloadNetworkConfig();

        return ReturnCode.OK;
    }
    #endregion

    #region Storage
    public Task<JobResult> ExpandVirtualHardDisk(string vhdPath, UInt64 sizeGB)
    {
        // Proxmox has no concept of resizing an arbitrary disk file by path outside of a VM's own
        // storage/disk configuration (see UpdateVirtualMachineAsync), so this is not supported.
        throw new NotSupportedException();
    }

    public Task<JobResult> ConvertVirtualHardDisk(string sourcePath, string destinationPath, VirtualHardDiskType diskType)
    {
        throw new NotSupportedException();
    }
    #endregion

    #region VNC Support
    static Dictionary<string, System.Net.IPAddress[]> ResolvedHosts = new Dictionary<string, System.Net.IPAddress[]>();

    public bool IsLocalAddress(string adr)
    {
        return Regex.IsMatch(adr, @"(^127\.)|(^192\.168\.)|(^10\.)|(^172\.1[6-9]\.)|(^172\.2[0-9]\.)|(^172\.3[0-1]\.)|(^\[?::1\]?$)|(^\[?[fF][cCdD])", RegexOptions.Singleline);
    }

    public bool IsHostLocal(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;

        var isHostIP = Regex.IsMatch(host, @"^[0.9]{1,3}(?:\.[0-9]{1,3}){3}$", RegexOptions.Singleline) || Regex.IsMatch(host, @"^\[?[0-9a-fA-F:]+\]?$", RegexOptions.Singleline);
        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]" || !isHostIP && !host.Contains('.') ||
            isHostIP && IsLocalAddress(host)) return true;

        if (!isHostIP)
        {
            IPAddress[] ips;
            lock (ResolvedHosts)
            {
                if (!ResolvedHosts.TryGetValue(host, out ips))
                {
                    ResolvedHosts.Add(host, ips = Dns.GetHostEntry(host).AddressList);
                }
            }
            return ips
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .All(ip => IsLocalAddress(ip.ToString()));
        }
        return false;
    }

    class VncConnection : IEquatable<VncConnection>, IDisposable
    {
        public VMId Vm { get; set; }
        public string Password { get; set; }
        public bool Equals(VncConnection other) => Vm.Equals(other.Vm) && Password == other.Password;
        public override int GetHashCode() => Vm.GetHashCode() ^ Password.GetHashCode();
        public virtual void Dispose() { }
    }

    class ConnectedVncConnection : VncConnection, IEquatable<VncConnection>, IDisposable
    {
        public int Port { get; set; }
        public string Ticket { get; set; }
        public Task<TunnelSocket> Tunnel { get; set; }
        public DateTime Requested { get; set; }

        bool isDisposed = false;
        public override void Dispose()
        {
            if (!isDisposed)
            {
                isDisposed = true;
                Tunnel?.Dispose();
            }
        }
    }

    static ConcurrentDictionary<VncConnection, VncConnection> VncConnections = new ConcurrentDictionary<VncConnection, VncConnection>();

    public async Task<VncCredentials> GetPveVncCredentials(string vmId)
    {
        var id = new VMId(vmId);
        if (id.Type == VirtualMachineType.Qemu)
        {
            var node = (await Api()).Nodes[id.Node];
            var vnc = await node.Qemu[id.Id].Vncproxy.Vncproxy(true, true);
            var vncRequested = DateTime.UtcNow;
            var dic = vnc.ResponseToDictionary;
            var data = dic["data"] as IDictionary<string, object>;
            var ticket = data["ticket"] as string;
            var password = data["password"] as string;
            var port = int.Parse(data["port"] as string);

            var credentials = new VncCredentials() { Password = password };

            var connection = new ConnectedVncConnection()
            {
                Vm = id,
                Ticket = ticket,
                Port = port,
                Password = password,
                Requested = DateTime.UtcNow
            };

            // fire and forget GetPveWebScocketAsync
            connection.Tunnel = GetPveWebSocket(connection);

            VncConnections.AddOrUpdate(connection, connection, (key, con) => connection);

            return credentials;
        } else throw new NotSupportedException("VNC is only supported for QEMU/KVM virtual machines.");
    }

    public async Task<TunnelSocket> GetPveVncWebSocketAsync(string vmId, VncCredentials credentials)
    {
        var vm = new VMId(vmId);
        var key = new VncConnection() { Password = credentials.Password, Vm = vm };
        var connection = VncConnections.GetOrAdd(key, key);
        if (connection != null && connection is ConnectedVncConnection opencon)
        {
            try
            {
                return await opencon.Tunnel;
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }
        else throw new Exception("GetPveWebSocketAsync called without requesting credentials first with GetPveVncCredentials");
    }
    async Task<TunnelSocket> GetPveWebSocket(ConnectedVncConnection connection)
    {
        var ip = string.IsNullOrEmpty(ServerName) ? "127.0.0.1" : ServerName;
        var url = $"wss://{ip}:{ServerPort}/api2/json/nodes/{connection.Vm.Node}/qemu/{connection.Vm.Id}/vncwebsocket?port={connection.Port}&vncticket={Uri.EscapeDataString(connection.Ticket)}";

        var api = await Api();
        var result = await api.Nodes[connection.Vm.Node].Qemu[connection.Vm.Id].Vncwebsocket.Vncwebsocket(connection.Port, connection.Ticket);

        if (result.ResponseInError) throw new ArgumentException($"PVE Api Vncwebsocket returned error: {result.GetError()}");

        var tunnel = new TunnelSocket(url);
        var cookie = new Cookie(nameof(api.PVEAuthCookie), api.PVEAuthCookie, "/", ServerName);
        tunnel.Cookies.Add(cookie);
        tunnel.HttpHeaders.Add(nameof(api.CSRFPreventionToken), api.CSRFPreventionToken);
        tunnel.ValidateCertificate = ValidateCertificate;
        tunnel.CanUpgrade = false;
        tunnel.Disposing += (sender, args) =>
        {
            VncConnections.TryRemove(connection, out VncConnection con);
        };

        try
        {
            await tunnel.ConnectAsync();
        }
        catch (Exception ex)
        {
            throw new IOException(ex.Message, ex);
        }

        var connectDelay = DateTime.Now.Subtract(connection.Requested);

        if (connectDelay > TimeSpan.FromSeconds(10)) Console.WriteLine("Warning: VNC Websocket access was slower than 10 seconds timeout value");

        Console.WriteLine($"VNC Websocket access delay: {connectDelay}");

        return tunnel;
    }

    bool IsApiServerLoopback => DnsService.IsHostLoopback(ServerName);
    bool IsApiIpV4 => DnsService.IsHostIpV4(ServerName);
    #endregion


    #region Jobs
    private async Task<JobResult> ToJobResult(Result result)
    {
        if (result == null) return new JobResult() { ReturnValue = ReturnCode.NotSupported };
        if (!result.IsSuccessStatusCode) return new JobResult() { ReturnValue = ReturnCode.Failed };

        string upid = result.Response.data as string;
        if (string.IsNullOrEmpty(upid) || !upid.StartsWith("UPID:"))
        {
            // The operation already completed synchronously; there is no background task to track.
            return new JobResult() { ReturnValue = ReturnCode.OK };
        }

        return new JobResult()
        {
            Job = await GetJob(upid),
            ReturnValue = ReturnCode.JobStarted
        };
    }

    private async Task<ConcreteJob> WaitForJob(string upid, int wait = 1000, long timeout = 10 * 60 * 1000)
    {
        await (await Api()).WaitForTaskToFinishAsync(upid, wait, timeout);
        return await GetJob(upid);
    }

    private static ConcreteJob ToConcreteJob(JObject status)
    {
        var upid = (string)status["upid"];
        var type = (string)status["type"];
        var itemId = (string)status["id"];
        var starttimeToken = status["starttime"];
        var start = starttimeToken != null ?
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((long)starttimeToken) :
            DateTime.MinValue;

        // The single-task status endpoint reports "status"="running"/"stopped" plus a separate
        // "exitstatus" once stopped; the task-list endpoints instead add an "endtime" and reuse
        // "status" to hold the exit text (e.g. "OK") once finished. Handle both shapes.
        string exitStatus = (string)(status["exitstatus"] ?? (status["endtime"] != null ? status["status"] : null));
        bool running = exitStatus == null && (string)status["status"] != "stopped";

        ConcreteJobState jobState = running ? ConcreteJobState.Running :
            string.Equals(exitStatus, "OK", StringComparison.OrdinalIgnoreCase) ? ConcreteJobState.Completed :
            ConcreteJobState.Failed;

        return new ConcreteJob()
        {
            Id = upid,
            JobState = jobState,
            Cancellable = jobState == ConcreteJobState.Running,
            Caption = type,
            Description = $"{type} {itemId}".Trim(),
            StartTime = start,
            ElapsedTime = start,
            ErrorCode = jobState == ConcreteJobState.Failed ? 1 : 0,
            ErrorDescription = jobState == ConcreteJobState.Failed ? exitStatus : null,
            PercentComplete = jobState == ConcreteJobState.Running ? 0 : 100
        };
    }

    public async Task<ConcreteJob> GetJob(string jobId)
    {
        var node = PveClient.GetNodeFromTask(jobId);
        var result = await (await Api()).Nodes[node].Tasks[jobId].Status.ReadTaskStatus();
        if (!result.IsSuccessStatusCode) return null;

        JObject status = result.Response.data;
        return ToConcreteJob(status);
    }

    public async IAsyncEnumerable<ConcreteJob> GetAllJobs()
    {
        var result = await (await Api()).Cluster.Tasks.Tasks();
        if (!result.IsSuccessStatusCode) yield break;

        JArray tasks = result.Response.data;
        foreach (var job in tasks.OfType<JObject>().Select(ToConcreteJob)) yield return job;
    }

    public async Task<ChangeJobStateReturnCode> ChangeJobState(string jobId, ConcreteJobRequestedState newState)
    {
        switch (newState)
        {
            case ConcreteJobRequestedState.Terminate:
            case ConcreteJobRequestedState.Kill:
                var api = await Api();
                var node = PveClient.GetNodeFromTask(jobId);
                var result = await api.Nodes[node].Tasks[jobId].StopTask();
                return result.IsSuccessStatusCode ? ChangeJobStateReturnCode.OK : ChangeJobStateReturnCode.Failed;
            default:
                return ChangeJobStateReturnCode.NotSupported;
        }
    }
    #endregion

    #region Configuration
    public async Task<int> GetProcessorCoresNumber()
    {
        var result = await (await Api()).Nodes[Environment.MachineName].Status.Status();
        JObject status = result.Response.data;
        return (int)status["cpuinfo"]["cpus"];
    }
    #endregion
}
