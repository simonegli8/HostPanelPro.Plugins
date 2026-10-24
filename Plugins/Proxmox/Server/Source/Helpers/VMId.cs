using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HostPanelPro.Providers.Virtualization;
public class VMId: IEquatable<VMId>
{
    public string Node { get; set; }
    public VirtualMachineType Type { get; set; }
    public long Id { get; set; }

    public VMId() { }
    public VMId(string vmid): this()
    {
        var parts = vmid.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            throw new ArgumentException("Invalid VM ID format", nameof(vmid));
        Node = parts[0];
        Type = (VirtualMachineType)Enum.Parse(typeof(VirtualMachineType), parts[1], true);
        Id = long.Parse(parts[2]);
    }

    public bool Equals(VMId other) => Node == other.Node && Type == other.Type && Id == other.Id;

    public override int GetHashCode() => Node.GetHashCode() ^ Type.GetHashCode() ^ Id.GetHashCode();
}
