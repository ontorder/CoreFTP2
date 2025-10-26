using CoreFtp.Enum;
using System.Net;
namespace CoreFtp.Components.DnsResolution;

public interface IDnsResolver
{
    Task<IPEndPoint?> ResolveAsync(string endpoint, int port, IpVersion ipVersion = IpVersion.IpV4, CancellationToken token = default);
}
