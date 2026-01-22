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
            .UseMauiCommunityToolkit()
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
        builder.Services.AddTransient<IPlatformDirectoryService, AndroidDirectoryService>();
#elif IOS
        // 注册服务
        builder.Services.AddTransient<IPlatformDirectoryService, IosDirectoryService>();
#else
		builder.Services.AddTransient<IPlatformDirectoryService, DesktopDirectoryService>();	
#endif
		builder.Services.AddTransient<IAlertService,AlertService>();
        builder.Services.AddTransient<MainPageViewModel>();

		return builder.Build();
	}
}
