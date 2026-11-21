using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using CommunityToolkit.Mvvm.Messaging;
using FileShare.LocalSend;
using FileShare.Desktop.ViewModels;
using LocalSend.Core.Http.DtoV2;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Threading;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// LocalSend 设备并入设备列表：发现事件 → 列表项（蓝色徽章标记、可收藏）。
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
            new Mock<IPlatformDialogService>().Object,
            new Mock<Shyd.Prism.Dialogs.IDialogService>().Object,
            new Mock<Shyd.Prism.Regions.IRegionManager>().Object,
            new WeakReferenceMessenger(),
            new Mock<Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime>().Object,
            new ImmediateSynchronizationContext(),
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
        // 「双 LocalSend 徽章」优化：副标题不再重复文字，身份由蓝色徽章（IsLocalSend）标示
        Assert.DoesNotContain("LocalSend", item.MetaText);
        Assert.Contains("192.168.1.20", item.MetaText);
        // 收藏与协议无关：LocalSend 设备同样显示星标（收藏后可免确认接收，见 ConfirmReceiveAsync）
        Assert.True(item.CanFavorite);

        // 重复发现不应产生重复项
        host.Raise(h => h.PeerDiscovered += null, peer);
        Assert.Single(viewModel.Devices);
    }

    /// <summary>
    /// 外部协议更新经 WeakReferenceMessenger 送达主界面传输列表；Dispose 后不再接收。
    /// </summary>
    [Fact]
    public void ExternalTransferUpdate_DeliveredViaMessenger_AndStopsAfterDispose()
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

        var messenger = new WeakReferenceMessenger();
        var viewModel = new MainViewModel(
            serviceManager.Object,
            new Mock<IPlatformDialogService>().Object,
            new Mock<Shyd.Prism.Dialogs.IDialogService>().Object,
            new Mock<Shyd.Prism.Regions.IRegionManager>().Object,
            messenger,
            new Mock<Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime>().Object,
            new ImmediateSynchronizationContext(),
            NullLoggerFactory.Instance,
            settings.Object,
            new Mock<IThemeService>().Object);

        var update = new ExternalTransferUpdate(
            TransferId: "msg-1",
            FileName: "note.txt",
            FileSize: 10,
            TransferredSize: 10,
            Direction: TransferDirection.Receive,
            Status: TransferStatus.Completed,
            SenderId: "peer-1");

        messenger.Send(update);
        Assert.Contains(viewModel.TransferTasks, t => t.TransferId == "msg-1");

        viewModel.Dispose();

        var afterDispose = new ExternalTransferUpdate(
            TransferId: "msg-2",
            FileName: "note2.txt",
            FileSize: 20,
            TransferredSize: 20,
            Direction: TransferDirection.Receive,
            Status: TransferStatus.Completed,
            SenderId: "peer-1");
        messenger.Send(afterDispose);
        Assert.DoesNotContain(viewModel.TransferTasks, t => t.TransferId == "msg-2");
    }
}