using FileShare.Core.Services;
using FileShare.Mobile.Services;
using FileShare.Mobile.ViewModels;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using Syncfusion.Maui.Toolkit.Hosting;

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
#elif IOS
        // 注册服务
        builder.Services.AddSingleton<IPlatformDirectoryService, IosDirectoryService>();
#else
		builder.Services.AddSingleton<IPlatformDirectoryService, DesktopDirectoryService>();	
#endif
        builder.Services.AddSingleton<IFileShareServiceManager, FileShareServiceManager>((serviceProvider) =>
        {
            var platformDirectoryService = serviceProvider.GetRequiredService<IPlatformDirectoryService>();
            var isMobileOs = System.OperatingSystem.IsAndroid() || System.OperatingSystem.IsIOS();          
            var isTablet = isMobileOs && FileShare.Mobile.Helpers.DeviceTypeHelper.IsTablet();
            var deviceType = isTablet ? Core.Models.DeviceType.Tablet :
                             isMobileOs ? Core.Models.DeviceType.Mobile :
                             Core.Models.DeviceType.Desktop;

            // 根据需要把 isTablet 用于不同的逻辑：例如传不同 DeviceType 或启用不同 UI/行为
            return new FileShareServiceManager(
                platformDirectoryService,
                Microsoft.Maui.Devices.DeviceInfo.Name,
                deviceType);
        });
#if ANDROID
        builder.Services.AddSingleton<IFileTransferForegroundService, AndroidFileTransferForegroundService>();
#elif IOS
        builder.Services.AddSingleton<IFileTransferForegroundService, IosFileTransferForegroundService>();
#else
        builder.Services.AddSingleton<IFileTransferForegroundService, DefaultFileTransferForegroundService>();
#endif
        builder.Services.AddSingleton<IAlertService,AlertService>();
        builder.Services.AddSingleton<MainPageViewModel>();

		return builder.Build();
	}
}
