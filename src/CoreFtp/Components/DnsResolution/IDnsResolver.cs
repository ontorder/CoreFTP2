using CoreFtp.Enum;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
#nullable enable
namespace CoreFtp.Components.DnsResolution;

public interface IDnsResolver
{
    Task<IPEndPoint?> ResolveAsync(string endpoint, int port, IpVersion ipVersion = IpVersion.IpV4, CancellationToken token = default);
}
