using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileShare.Core.Network;

/// <summary>
/// 本地网卡地址工具：用于跳过本机地址、推荐扫描网段、以及多网卡环境下的地址选择。
/// </summary>
public static class LocalNetworkHelper
{
    /// <summary>
    /// 枚举所有「已启用、非回环、非隧道」网卡上的 IPv4 地址与子网掩码。
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> GetIPv4Interfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null)
                    {
                        continue;
                    }

                    result.Add((unicast.Address, mask));
                }
            }
        }
        catch
        {
            // 某些受限平台（如部分 iOS/Android 版本）会抛异常，调用方按「无网卡信息」处理
        }

        return result;
    }

    /// <summary>本机所有 IPv4 地址。</summary>
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        return GetIPv4Interfaces()
            .Select(item => item.Address)
            .Distinct()
            .ToList();
    }

    /// <summary>判断地址是否属于本机（用于扫描/探测时跳过自身）。</summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var local in GetLocalIPv4Addresses())
        {
            if (local.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 根据本机网卡推荐一个扫描网段，例如 <c>192.168.1.0/24</c>。
    /// 优先选择带网关的物理网卡。
    /// </summary>
    public static string? GetSuggestedCidr()
    {
        try
        {
            string? fallback = null;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork);

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null)
                    {
                        continue;
                    }

                    var cidr = ToCidr(unicast.Address, mask);
                    if (cidr is null)
                    {
                        continue;
                    }

                    if (hasGateway)
                    {
                        return cidr;
                    }

                    fallback ??= cidr;
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把「地址 + 掩码」换算成 CIDR 形式。</summary>
    public static string? ToCidr(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var prefix = 0;
        foreach (var b in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0)
                {
                    prefix++;
                }
                else
                {
                    bit = -1;
                }
            }
        }

        // 前缀过小意味着要扫描的地址太多，这里直接取 /24 作为保守推荐
        if (prefix is < 20 or > 32)
        {
            prefix = 24;
        }

        var network = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            network[i] = (byte)(addressBytes[i] & maskBytes[i]);
        }

        return $"{new IPAddress(network)}/{prefix}";
    }
}
