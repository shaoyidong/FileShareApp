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
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if ANDROID
		// 注册服务
		builder.Services.AddTransient<IPlatformDirectoryService, AndroidDirectoryService>();
#elif IOS
		// 注册服务
		builder.Services.AddTransient<IPlatformDirectoryService, IosDirectoryService>();
#else
		builder.Services.AddTransient<IPlatformDirectoryService, DesktopDirectoryService>();	
#endif
        builder.Services.AddTransient<MainPageViewModel>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
