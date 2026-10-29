using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using FileShare.Desktop.ViewModels;
using LocalSend.Core.Http.DtoV2;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Threading;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// LocalSend 设备并入设备列表：发现事件 → 列表项（带 LocalSend 标记、不可收藏）。
/// </summary>
public class LocalSendDeviceListTests
{
    [Fact]
    public void PeerDiscovered_AddsLocalSendDevice()
    {
        var serviceManager = new Mock<IFileShareServiceManager>();
        serviceManager.Setup(m => m.GetLocalDeviceInfo()).Returns(new DeviceInfo
        {
            DeviceId = "local",
            DeviceName = "Local",
            DeviceType = DeviceType.Desktop,
            Port = 5237,
            IpAddress = "127.0.0.1"
        });

        var settings = new Mock<IAppSettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings());

        var host = new Mock<ILocalSendInteropHost>();
        host.SetupGet(h => h.Peers).Returns(Array.Empty<LocalSendPeer>());

        var viewModel = new MainViewModel(
            serviceManager.Object,
            new Mock<IDialogService>().Object,
            new Mock<Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime>().Object,
            SynchronizationContext.Current ?? new SynchronizationContext(),
            NullLoggerFactory.Instance,
            settings.Object,
            new Mock<IThemeService>().Object,
            null,
            null,
            host.Object);

        var peer = new LocalSendPeer(
            DeviceId: "localsend:abc",
            Alias: "Phone",
            IpAddress: "192.168.1.20",
            Port: 53317,
            Protocol: ProtocolTypeV2.Http,
            Fingerprint: "abc");

        // 触发发现事件（模拟组播发现）
        host.Raise(h => h.PeerDiscovered += null, peer);

        var item = Assert.Single(viewModel.Devices);
        Assert.True(item.IsLocalSend);
        Assert.Equal("localsend:abc", item.DeviceId);
        Assert.Equal("Phone", item.DeviceName);
        Assert.Contains("LocalSend", item.MetaText);
        Assert.False(item.CanFavorite);

        // 重复发现不应产生重复项
        host.Raise(h => h.PeerDiscovered += null, peer);
        Assert.Single(viewModel.Devices);
    }
}