﻿using CoreFtp.Enum;
using CoreFtp.Infrastructure.Caching;
using System.Net;
using System.Net.Sockets;

namespace CoreFtp.Components.DnsResolution;

public sealed class DnsResolver : IDnsResolver
{
    private readonly ICache _cache;

    public DnsResolver()
        => _cache = new InMemoryCache();

    public async Task<IPEndPoint?> ResolveAsync(string endpoint, int port, IpVersion ipVersion = IpVersion.IpV4, CancellationToken cancellation = default)
    {
        string cacheKey = $"{endpoint}:{port}:{ipVersion}";

        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port));

        if (_cache.HasKey(cacheKey))
            return _cache.Get<IPEndPoint>(cacheKey);

        var addressFamily = ipVersion.HasFlag(IpVersion.IpV4)
            ? AddressFamily.InterNetwork
            : AddressFamily.InterNetworkV6;

        cancellation.ThrowIfCancellationRequested();

        IPEndPoint? ipEndpoint;

        var ipAddress = TryGetIpAddress(endpoint);
        if (ipAddress != null)
        {
            ipEndpoint = new IPEndPoint(ipAddress, port);
            _cache.Add(cacheKey, ipEndpoint, TimeSpan.FromMinutes(60));
            return ipEndpoint;
        }

        try
        {
            var allAddresses = await Dns.GetHostAddressesAsync(endpoint, cancellation);

            var firstAddressInFamily = allAddresses.FirstOrDefault(x => x.AddressFamily == addressFamily);
            if (firstAddressInFamily != null)
            {
                ipEndpoint = new IPEndPoint(firstAddressInFamily, port);
                _cache.Add(cacheKey, ipEndpoint, TimeSpan.FromMinutes(60));
                return ipEndpoint;
            }

            if (addressFamily == AddressFamily.InterNetwork && ipVersion.HasFlag(IpVersion.IpV6))
            {
                ipEndpoint = await ResolveAsync(endpoint, port, IpVersion.IpV6, cancellation);
                if (ipEndpoint != null)
                {
                    _cache.Add(cacheKey, ipEndpoint, TimeSpan.FromMinutes(60));
                    return ipEndpoint;
                }
            }

            var firstAddress = allAddresses.FirstOrDefault();
            if (firstAddress == null)
                return null;

            switch (firstAddress.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    ipEndpoint = new IPEndPoint(firstAddress.MapToIPv6(), port);
                    break;

                case AddressFamily.InterNetworkV6:
                    ipEndpoint = new IPEndPoint(firstAddress.MapToIPv4(), port);
                    break;

                default:
                    return null;
            }

            _cache.Add(cacheKey, ipEndpoint, TimeSpan.FromMinutes(60));

            return ipEndpoint;
        }
        catch
        {
            return null;
        }
    }

    private static IPAddress? TryGetIpAddress(string endpoint)
    {
        var tokens = endpoint.Split(':');

        string endpointToParse = endpoint;

        if (tokens.Length == 0)
            return null;

        if (tokens.Length <= 2)
        {
            // IPv4
            endpointToParse = tokens[0];
        }
        else if (tokens.Length > 2)
        {
            // IPv6
            endpointToParse = tokens[0].StartsWith("[") && tokens[^2].EndsWith("]")
                ? string.Join(":", tokens.Take(tokens.Length - 1).ToArray())
                : endpoint;
        }

        return IPAddress.TryParse(endpointToParse, out IPAddress? address)
            ? address
            : null;
    }
}
