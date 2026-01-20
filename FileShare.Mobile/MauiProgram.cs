using FileShare.Core.Services;
using FileShare.Mobile.Services;
using FileShare.Mobile.ViewModels;
using Microsoft.Extensions.Logging;

namespace FileShare.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
            // Initialize the .NET MAUI Community Toolkit by adding the below line of code
            .UseMauiApp<App>()
            // After initializing the .NET MAUI Community Toolkit, optionally add additional fo
            .ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

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

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
