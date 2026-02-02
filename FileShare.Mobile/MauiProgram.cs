using FileShare.Core.Services;
using FileShare.Mobile.Services;
using FileShare.Mobile.ViewModels;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using Syncfusion.Maui.Toolkit.Hosting;
using System.IO;
using Microsoft.Maui.Storage;
using FileShare.Mobile.Views;

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

#if DEBUG
        builder.Logging.AddDebug();
        builder.Services.AddLogging(configure => configure.AddDebug());
#endif

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
        builder.Services.AddSingleton<IPermissionService, DefaultPermissionService>();
        builder.Services.AddSingleton<IFileTransferForegroundService, IosFileTransferForegroundService>();
#else
		builder.Services.AddSingleton<IPlatformDirectoryService, DesktopDirectoryService>();
        builder.Services.AddSingleton<IAppManagementService, DefaultAppManagementService>();
        builder.Services.AddSingleton<IPermissionService, DefaultPermissionService>();
        builder.Services.AddSingleton<IFileTransferForegroundService, DefaultFileTransferForegroundService>();
#endif
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

            // 根据需要把 isTablet 用于不同的逻辑：例如传不同 DeviceType 或启用不同 UI/行为
            return new FileShareServiceManager(
                platformDirectoryService,
                databaseService,
                Microsoft.Maui.Devices.DeviceInfo.Name,
                deviceType);
        });

        builder.Services.AddSingleton<INavigation>((serviceProvider) => Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault()?.Navigation!);
        builder.Services.AddSingleton<IAlertService,AlertService>();
        builder.Services.AddSingleton<IPickerService,MauiPickerService>();
        builder.Services.AddSingleton<MainPageViewModel>();
        builder.Services.AddSingletonWithShellRoute<AppListPage, AppListViewModel>("AppListPage");

        return builder.Build();
	}
}
