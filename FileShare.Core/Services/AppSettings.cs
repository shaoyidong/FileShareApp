using FileShare.Core.Models;

namespace FileShare.Core.Services;

/// <summary>
/// 界面皮肤。用同一套语义化样式键在不同皮肤文件中实现，因此切换皮肤不会影响布局。
/// </summary>
public enum AppSkin
{
    /// <summary>Windows 7 / Aero 风格（默认）。</summary>
    Win7 = 0,

    /// <summary>Windows XP / Luna 风格。</summary>
    WinXP = 1,
}

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

    /// <summary>界面皮肤。</summary>
    public AppSkin Skin { get; set; } = AppSkin.Win7;

    /// <summary>收藏（信任）的设备 ID 列表。</summary>
    public List<string> FavoriteDeviceIds { get; set; } = new();

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
            Skin = Skin,
            FavoriteDeviceIds = new List<string>(FavoriteDeviceIds)
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

        if (!Enum.IsDefined(Skin))
        {
            Skin = AppSkin.Win7;
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
    }
}
