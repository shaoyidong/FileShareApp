using FileShare.Core.Models;

namespace FileShare.Core.Services;


/// <summary>
/// 应用设置。对应 LocalSend 的「设置」页：设备名、设备类型、保存目录、自动接收、加密开关等。
/// <para>所有字段都必须可被 <see cref="System.Text.Json"/> 序列化（AOT 友好，使用源生成上下文）。</para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>本机显示名称，默认取计算机名。</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>本机设备类型（影响对端列表里显示的图标）。</summary>
    public DeviceType DeviceType { get; set; } = DeviceType.Desktop;

    /// <summary>文件传输监听端口。</summary>
    public int TransferPort { get; set; } = 5237;

    /// <summary>是否启用 TLS 加密传输。</summary>
    public bool TlsEnabled { get; set; } = true;

    /// <summary>
    /// 是否自动接收「收藏设备」发来的文件（对应 LocalSend 的 Quick Save）。
    /// </summary>
    public bool AutoAcceptFromFavorites { get; set; }

    /// <summary>
    /// 固定保存目录。为空时按文件类型保存到图片/视频/音乐/文档/下载目录（与 LocalSend 一致）。
    /// </summary>
    public string? SaveDirectory { get; set; }


    /// <summary>收藏（信任）的设备 ID 列表。</summary>
    public List<string> FavoriteDeviceIds { get; set; } = new();

    /// <summary>接收同名文件时的处理策略（默认自动重命名，绝不静默覆盖）。</summary>
    public FileNameConflictPolicy ConflictPolicy { get; set; } = FileNameConflictPolicy.Rename;

    /// <summary>界面语言：<c>zh-CN</c> / <c>en-US</c>（需要重启生效）。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>最小化窗口时隐藏到系统托盘。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>关闭主窗口时隐藏到托盘（而不是退出）。</summary>
    public bool CloseToTray { get; set; }

    /// <summary>是否启用 mDNS 发现。</summary>
    public bool EnableMdns { get; set; } = true;

    /// <summary>是否启用 UDP 组播发现。</summary>
    public bool EnableMulticast { get; set; } = true;

    /// <summary>是否启用 UDP 广播发现。</summary>
    public bool EnableBroadcast { get; set; } = true;

    /// <summary>组播地址（默认 224.0.0.167，与 LocalSend 一致）。</summary>
    public string MulticastAddress { get; set; } = "224.0.0.167";

    /// <summary>组播端口（默认 53317）。</summary>
    public int MulticastPort { get; set; } = 53317;

    /// <summary>广播端口（默认 5236）。</summary>
    public int BroadcastPort { get; set; } = 5236;

    /// <summary>多文件同时发送的并发数量（1~4，1 表示逐个发送）。</summary>
    public int ParallelTransfers { get; set; } = 1;

    /// <summary>发送限速（KB/s），0 表示不限速。</summary>
    public int UploadLimitKBps { get; set; }

    /// <summary>是否启用「网页分享」（浏览器上传，无需安装 App）。</summary>
    public bool WebShareEnabled { get; set; }

    /// <summary>网页分享端口（默认 8088）。</summary>
    public int WebSharePort { get; set; } = 8088;

    /// <summary>网页分享 PIN（6 位数字；为空时首次启用自动生成）。</summary>
    public string WebSharePin { get; set; } = string.Empty;

    /// <summary>发现用的网段白名单（逗号分隔，支持 CIDR；为空=不限制）。</summary>
    public string NetworkWhitelist { get; set; } = string.Empty;

    /// <summary>发现用的网段黑名单（优先级高于白名单）。</summary>
    public string NetworkBlacklist { get; set; } = string.Empty;

    /// <summary>是否启用「与官方 LocalSend 互通」（本机作为 LocalSend 接收端）。</summary>
    public bool LocalSendEnabled { get; set; }

    /// <summary>LocalSend 服务端口（官方默认 53317）。</summary>
    public int LocalSendPort { get; set; } = 53317;

    /// <summary>LocalSend 接收 PIN（可空，对应官方客户端的 PIN 校验）。</summary>
    public string LocalSendPin { get; set; } = string.Empty;

    /// <summary>是否收藏了指定设备。</summary>
    public bool IsFavorite(string? deviceId)
    {
        return !string.IsNullOrEmpty(deviceId) && FavoriteDeviceIds.Contains(deviceId);
    }

    /// <summary>
    /// 切换收藏状态。
    /// </summary>
    /// <returns>切换后是否为收藏状态。</returns>
    public bool ToggleFavorite(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        if (FavoriteDeviceIds.Remove(deviceId))
        {
            return false;
        }

        FavoriteDeviceIds.Add(deviceId);
        return true;
    }

    /// <summary>深拷贝（保存时避免读写竞争）。</summary>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            DeviceName = DeviceName,
            DeviceType = DeviceType,
            TransferPort = TransferPort,
            TlsEnabled = TlsEnabled,
            AutoAcceptFromFavorites = AutoAcceptFromFavorites,
            SaveDirectory = SaveDirectory,
            FavoriteDeviceIds = new List<string>(FavoriteDeviceIds),
            ConflictPolicy = ConflictPolicy,
            Language = Language,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            EnableMdns = EnableMdns,
            EnableMulticast = EnableMulticast,
            EnableBroadcast = EnableBroadcast,
            MulticastAddress = MulticastAddress,
            MulticastPort = MulticastPort,
            BroadcastPort = BroadcastPort,
            ParallelTransfers = ParallelTransfers,
            UploadLimitKBps = UploadLimitKBps,
            WebShareEnabled = WebShareEnabled,
            WebSharePort = WebSharePort,
            WebSharePin = WebSharePin,
            NetworkWhitelist = NetworkWhitelist,
            NetworkBlacklist = NetworkBlacklist,
            LocalSendEnabled = LocalSendEnabled,
            LocalSendPort = LocalSendPort,
            LocalSendPin = LocalSendPin
        };
    }

    /// <summary>修正非法值，保证运行期不会因为脏配置崩溃。</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            DeviceName = Environment.MachineName;
        }

        DeviceName = DeviceName.Trim();
        if (DeviceName.Length > 64)
        {
            DeviceName = DeviceName[..64];
        }

        if (TransferPort is < 1024 or > 65535)
        {
            TransferPort = 5237;
        }

        if (!Enum.IsDefined(DeviceType))
        {
            DeviceType = DeviceType.Desktop;
        }


        if (FavoriteDeviceIds is null)
        {
            FavoriteDeviceIds = new List<string>();
        }
        else
        {
            FavoriteDeviceIds = FavoriteDeviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();
        }

        SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory) ? null : SaveDirectory.Trim();

        if (!Enum.IsDefined(ConflictPolicy))
        {
            ConflictPolicy = FileNameConflictPolicy.Rename;
        }

        if (Language is not ("zh-CN" or "en-US"))
        {
            Language = "zh-CN";
        }

        if (MulticastPort is < 1024 or > 65535)
        {
            MulticastPort = 53317;
        }

        if (BroadcastPort is < 1024 or > 65535)
        {
            BroadcastPort = 5236;
        }

        if (ParallelTransfers is < 1 or > 4)
        {
            ParallelTransfers = 1;
        }

        if (UploadLimitKBps < 0 || UploadLimitKBps > 1_000_000)
        {
            UploadLimitKBps = 0;
        }

        if (WebSharePort is < 1024 or > 65535)
        {
            WebSharePort = 8088;
        }

        WebSharePin = (WebSharePin ?? string.Empty).Trim();
        NetworkWhitelist = (NetworkWhitelist ?? string.Empty).Trim();
        NetworkBlacklist = (NetworkBlacklist ?? string.Empty).Trim();

        if (NetworkWhitelist.Length > 512)
        {
            NetworkWhitelist = NetworkWhitelist[..512];
        }

        if (NetworkBlacklist.Length > 512)
        {
            NetworkBlacklist = NetworkBlacklist[..512];
        }

        if (LocalSendPort is < 1024 or > 65535)
        {
            LocalSendPort = 53317;
        }

        LocalSendPin = (LocalSendPin ?? string.Empty).Trim();

        if (!System.Net.IPAddress.TryParse(MulticastAddress, out var multicast) ||
            multicast.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            MulticastAddress = "224.0.0.167";
        }
    }
}
