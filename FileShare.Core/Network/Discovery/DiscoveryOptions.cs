using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Network.Discovery
{
    public class DiscoveryOptions
    {
        //public bool EnableUdpMulticastPeriodicAnnouncement { get; init; } = false;
        //public bool EnableUdpBroadcastPeriodicAnnouncement { get; init; } = false;
        //public bool EnableMdnsPeriodicAnnouncement { get; init; } = true;
        public int UdpMulticastPort { get; init; } = 53317;
        public int UdpBroadcastPort { get; init; } = 5236;
        public string? UdpMulticastAddress { get; init; } = "224.0.0.167";
    }
}
