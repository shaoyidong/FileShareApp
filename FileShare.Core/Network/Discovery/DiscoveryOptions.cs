namespace FileShare.Core.Network.Discovery
{
    /// <summary>
    /// 自动发现配置。
    /// <para>对应设置页的「发现方式」：三路发现可以单独开关，地址/端口也可自定义，
    /// 便于在网络环境特殊（组播被禁用、端口冲突、与 LocalSend 共存等）时按需调整。</para>
    /// </summary>
    public class DiscoveryOptions
    {
        /// <summary>是否启用 mDNS（<c>_fileshare._tcp.local</c>，5353）。</summary>
        public bool EnableMdns { get; init; } = true;

        /// <summary>是否启用 UDP 组播发现。</summary>
        public bool EnableUdpMulticast { get; init; } = true;

        /// <summary>是否启用 UDP 广播发现。</summary>
        public bool EnableUdpBroadcast { get; init; } = true;

        /// <summary>组播端口（默认 53317）。</summary>
        public int UdpMulticastPort { get; init; } = 53317;

        /// <summary>广播端口（默认 5236）。</summary>
        public int UdpBroadcastPort { get; init; } = 5236;

        /// <summary>组播地址（默认 224.0.0.167，与 LocalSend 一致）。</summary>
        public string? UdpMulticastAddress { get; init; } = "224.0.0.167";

        /// <summary>允许参与发现的网段（白名单，空=不限制）。</summary>
        public string? NetworkWhitelist { get; init; }

        /// <summary>排除的网段（黑名单）。</summary>
        public string? NetworkBlacklist { get; init; }

        /// <summary>由用户设置生成发现配置。</summary>
        public static DiscoveryOptions FromSettings(FileShare.Core.Services.AppSettings settings)
        {
            return new DiscoveryOptions
            {
                EnableMdns = settings.EnableMdns,
                EnableUdpMulticast = settings.EnableMulticast,
                EnableUdpBroadcast = settings.EnableBroadcast,
                UdpMulticastPort = settings.MulticastPort,
                UdpBroadcastPort = settings.BroadcastPort,
                UdpMulticastAddress = settings.MulticastAddress,
                NetworkWhitelist = settings.NetworkWhitelist,
                NetworkBlacklist = settings.NetworkBlacklist
            };
        }
    }
}
