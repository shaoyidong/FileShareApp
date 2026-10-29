using FileShare.Core.Services;
using FileShare.Mobile.Helpers;
using FileShare.Mobile.Services;
using FileShare.Mobile.ViewModels;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using Syncfusion.Maui.Toolkit.Hosting;
using System.IO;
using Microsoft.Maui.Storage;
using FileShare.Mobile.Views;
using Serilog;

namespace FileShare.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit(
#if WINDOWS
	options =>
  {
    options.SetShouldEnableSnackbarOnWindows(true);
  }
#endif
            )
            .ConfigureSyncfusionToolkit()
            .ConfigureMauiHandlers(handlers =>
            {
#if WINDOWS
	    				Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("KeyboardAccessibleCollectionView", (handler, view) =>
	    				{
	    					handler.PlatformView.SingleSelectionFollowsFocus = false;
	    				});
#endif
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("SegoeUI-Semibold.ttf", "SegoeSemibold");
                fonts.AddFont("FluentSystemIcons-Regular.ttf", FluentUI.FontFamily);
            });

        // 初始化 Serilog（Debug 输出 + 按天滚动文件），对齐 Desktop 项目
        var logDir = Path.Combine(FileSystem.AppDataDirectory, "logs");
        var serilogLogger = SerilogSetup.CreateLogger(logDir);
        Log.Logger = serilogLogger;
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilogLogger, dispose: true);

        // Continue initializing your .NET MAUI App here
#if ANDROID
        // 注册服务
        builder.Services.AddSingleton<IPlatformDirectoryService, AndroidDirectoryService>();
        builder.Services.AddSingleton<IAppManagementService, AndroidAppManagementService>();
        builder.Services.AddSingleton<IPermissionService, AndroidPermissionService>();
        builder.Services.AddSingleton<IFileTransferForegroundService, AndroidFileTransferForegroundService>();
#elif IOS
        // 注册服务
        builder.Services.AddSingleton<IPlatformDirectoryService, IosDirectoryService>();
        builder.Services.AddSingleton<IAppManagementService, DefaultAppManagementService>();
        builder.Services.AddSingleton<IPermissionService, IosPermissionService>();
        builder.Services.AddSingleton<IFileTransferForegroundService, IosFileTransferForegroundService>();
#else
		builder.Services.AddSingleton<IPlatformDirectoryService, DesktopDirectoryService>();
        builder.Services.AddSingleton<IAppManagementService, DefaultAppManagementService>();
        builder.Services.AddSingleton<IPermissionService, DefaultPermissionService>();
        builder.Services.AddSingleton<IFileTransferForegroundService, DefaultFileTransferForegroundService>();
#endif
        builder.Services.AddSingleton<IAppSettingsService>((serviceProvider) =>
        {
            var settingsPath = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
            return new JsonAppSettingsService(settingsPath, serviceProvider.GetService<ILoggerFactory>());
        });

        builder.Services.AddSingleton<IDatabaseService>((serviceProvider) =>
        {
            // 获取应用数据目录
            var appDataDirectory = FileSystem.AppDataDirectory;
            var databasePath = Path.Combine(appDataDirectory, "fileshare.db");
            return new DatabaseService(databasePath);
        });

        builder.Services.AddSingleton<IFileShareServiceManager, FileShareServiceManager>((serviceProvider) =>
        {
            var platformDirectoryService = serviceProvider.GetRequiredService<IPlatformDirectoryService>();
            var databaseService = serviceProvider.GetRequiredService<IDatabaseService>();
            var isMobileOs = System.OperatingSystem.IsAndroid() || System.OperatingSystem.IsIOS();
            var isTablet = isMobileOs && FileShare.Mobile.Helpers.DeviceTypeHelper.IsTablet();
            var deviceType = isTablet ? Core.Models.DeviceType.Tablet :
                             isMobileOs ? Core.Models.DeviceType.Mobile :
                             Core.Models.DeviceType.Desktop;

            // 从 DI 容器获取日志工厂（DEBUG 下已注册 AddDebug），未注册时返回 null（使用 NullLogger）
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

            // 设置：加密默认开启、端口、同名策略、限速、发现方式、设备名
            var settingsService = serviceProvider.GetRequiredService<IAppSettingsService>();
            var settings = settingsService.Current;

            var deviceName = string.IsNullOrWhiteSpace(settings.DeviceName)
                ? Microsoft.Maui.Devices.DeviceInfo.Name
                : settings.DeviceName;

            var manager = new FileShareServiceManager(
                platformDirectoryService,
                databaseService,
                deviceName,
                deviceType,
                transferPort: settings.TransferPort,
                discoveryOptions: Core.Network.Discovery.DiscoveryOptions.FromSettings(settings),
                tlsEnabled: settings.TlsEnabled,
                loggerFactory: loggerFactory,
                conflictPolicy: settings.ConflictPolicy,
                uploadLimitKBps: settings.UploadLimitKBps);

            loggerFactory?.CreateLogger("MauiProgram").LogInformation(
                "FileShareServiceManager 已创建: 设备名={DeviceName}, 类型={DeviceType}, 端口={Port}, 加密={Tls}, 同名策略={ConflictPolicy}, 限速={Limit}KB/s",
                deviceName,
                deviceType,
                settings.TransferPort,
                settings.TlsEnabled,
                settings.ConflictPolicy,
                settings.UploadLimitKBps);

            return manager;
        });

        builder.Services.AddSingleton<INavigation>((serviceProvider) => Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault()?.Navigation!);
        builder.Services.AddSingleton<IAlertService,AlertService>();
        builder.Services.AddSingleton<IPickerService,MauiPickerService>();
        builder.Services.AddSingleton<MainPageViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingletonWithShellRoute<AppListPage, AppListViewModel>("AppListPage");

        // 注册 ViewLocator（对齐 Desktop 项目的 ViewLocator 模式）
        builder.Services.AddSingleton<ViewLocator>();

        return builder.Build();
	}
}
