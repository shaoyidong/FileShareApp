using System.Net;
using FileShare.Core.Models;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// 单设备探测服务：向指定地址发一个发现报文，读取对端回包从而取得完整的设备信息。
/// <para>它解决两个在真实局域网里非常常见的问题：</para>
/// <list type="bullet">
/// <item>设备处于不同子网（广播/组播过不去），但三层路由可达 —— 直接给 IP 即可；</item>
/// <item>对端防火墙拦截了入站 UDP 广播，但服务端口仍然对 TCP 开放 —— 退化为 TCP 探活。</item>
/// </list>
/// <para>UDP 探测伪装成一次普通的发现问答（与 <c>UdpBroadcastDiscoveryService</c> 使用同一报文格式），
/// 因此对端无需任何改动即可应答；TCP 探活仅用于确认端口存活，此时拿不到设备身份，
/// 会返回一个以 IP 命名的占位设备，仍然可以直接发送文件。</para>
/// </summary>
public interface IDeviceProbeService
{
    /// <summary>
    /// 探测用户输入的地址（IP、IP:端口、主机名）。
    /// </summary>
    Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// 探测指定地址与端口。
    /// </summary>
    Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default);
}
