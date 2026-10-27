using System.Net;

namespace FileShare.Core.Network;

/// <summary>
/// 网段白名单/黑名单过滤。
/// <para>用于多网卡（有线 + 无线 + VPN + 虚拟网卡）环境：只允许在指定网段广播/组播，
/// 避免把发现报文发到 VPN、Hyper-V 虚拟网卡或不该暴露的网段。</para>
/// <para>规则写法：逗号/分号/换行分隔，支持 <c>192.168.1.0/24</c>、<c>192.168.1.5</c>、
/// 以及省略掩码的 <c>192.168.1.</c>（等价 /24）。</para>
/// </summary>
public static class NetworkFilter
{
    /// <summary>
    /// 判断某个地址是否允许参与发现。
    /// </summary>
    /// <param name="address">本机网卡地址或对端地址。</param>
    /// <param name="whitelist">白名单（为空表示不限制）。</param>
    /// <param name="blacklist">黑名单（优先于白名单）。</param>
    public static bool IsAllowed(IPAddress address, string? whitelist, string? blacklist)
    {
        if (address is null)
        {
            return false;
        }

        var black = ParseRules(blacklist);
        foreach (var rule in black)
        {
            if (rule.Contains(address))
            {
                return false;
            }
        }

        var white = ParseRules(whitelist);
        if (white.Count == 0)
        {
            return true;
        }

        foreach (var rule in white)
        {
            if (rule.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是否存在任何过滤规则。</summary>
    public static bool HasRules(string? whitelist, string? blacklist)
    {
        return ParseRules(whitelist).Count > 0 || ParseRules(blacklist).Count > 0;
    }

    private static List<IpRange> ParseRules(string? text)
    {
        var rules = new List<IpRange>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rules;
        }

        foreach (var raw in text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (IpRange.TryParse(entry, out var range))
            {
                rules.Add(range);
            }
        }

        return rules;
    }

    private readonly struct IpRange
    {
        private readonly uint _network;
        private readonly uint _mask;

        private IpRange(uint network, uint mask)
        {
            _network = network & mask;
            _mask = mask;
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            var value = ToUInt32(address);
            return (value & _mask) == _network;
        }

        public static bool TryParse(string text, out IpRange range)
        {
            range = default;

            var parts = text.Split('/', 2);
            var addressText = parts[0].Trim();

            // 支持 "192.168.1." 这种前缀写法
            if (addressText.EndsWith('.') && parts.Length == 1)
            {
                addressText += "0";
                parts = new[] { addressText, "24" };
            }

            if (!IPAddress.TryParse(addressText, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            var prefix = 32;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
            {
                return false;
            }

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            range = new IpRange(ToUInt32(address), mask);
            return true;
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
    }
}
