using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Dispatch.Api.Incus;

/// <summary>Computes the DISPATCH_URL containers use to reach this API.</summary>
public sealed class PublicUrlResolver(IOptions<DispatchOptions> options, ILogger<PublicUrlResolver> logger)
{
    private string? _cached;

    public string Resolve()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.PublicUrlForContainers))
        {
            return _cached = o.PublicUrlForContainers.TrimEnd('/');
        }

        var ip = FindIPv4(o.IncusBridge);
        if (ip is null)
        {
            logger.LogWarning("Could not find an IPv4 address on bridge {Bridge}; falling back to first non-loopback IPv4", o.IncusBridge);
            ip = FindIPv4(null) ?? "127.0.0.1";
        }

        _cached = $"http://{ip}:{o.BindPort}";
        logger.LogInformation("DISPATCH_URL for containers: {Url}", _cached);
        return _cached;
    }

    private static string? FindIPv4(string? interfaceName)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (interfaceName is not null && !string.Equals(nic.Name, interfaceName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (interfaceName is null && nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (nic.OperationalStatus != OperationalStatus.Up && interfaceName is null)
                {
                    continue;
                }

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return null;
    }
}
