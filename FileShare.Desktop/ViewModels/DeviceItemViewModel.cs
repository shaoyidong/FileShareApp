using CommunityToolkit.Mvvm.ComponentModel;
using FileShare.Core.Models;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 设备列表项。包一层是为了在不改动 Core 模型（会被序列化进发现报文）的前提下，
/// 加上「收藏」「离线」等纯界面状态。
/// </summary>
public partial class DeviceItemViewModel : ViewModelBase
{
    public DeviceItemViewModel(DeviceInfo device, bool isFavorite = false)
    {
        Device = device;
        _isFavorite = isFavorite;
    }

    /// <summary>底层设备模型。</summary>
    public DeviceInfo Device { get; }

    public string DeviceId => Device.DeviceId;

    public string DeviceName => string.IsNullOrWhiteSpace(Device.DeviceName) ? Device.IpAddress : Device.DeviceName;

    public string IpAddress => Device.IpAddress;

    /// <summary>设备类型的中文说明。</summary>
    public string DeviceTypeText => Device.DeviceType switch
    {
        DeviceType.Mobile => Loc.Get("手机"),
        DeviceType.Tablet => Loc.Get("平板"),
        _ => Loc.Get("桌面")
    };

    /// <summary>第二行副标题：类型 · IP · 加密标记。</summary>
    public string MetaText => $"{DeviceTypeText} · {Device.IpAddress}" + (Device.SupportsTls ? Loc.Get(" · 加密") : string.Empty);

    [ObservableProperty]
    private bool _isFavorite;

    public bool IsNotFavorite => !IsFavorite;

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotFavorite));
    }

    /// <summary>收藏后排到列表最前面，便于下次快速找到。</summary>
    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(IpAddress));
        OnPropertyChanged(nameof(DeviceTypeText));
        OnPropertyChanged(nameof(MetaText));
    }
}
