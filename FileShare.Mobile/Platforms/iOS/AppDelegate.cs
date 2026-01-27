using Foundation;
using UIKit;
using FileShare.Mobile.Services;
using System;

namespace FileShare.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private IFileTransferForegroundService? _foregroundService;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
	
	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
        _foregroundService = IPlatformApplication.Current?.Services?
.GetService<IFileTransferForegroundService>();
        return base.FinishedLaunching(application, launchOptions);       
    }
	
	public override void DidEnterBackground(UIApplication application)
	{
		base.DidEnterBackground(application);
		
		//// 应用进入后台时，确保文件传输前台服务已启动
		//var service = IosFileTransferForegroundService.Instance;
		//if (service != null)
		//{
		//	_ = service.StartServiceAsync();
		//}
	}
	
	public override void WillEnterForeground(UIApplication application)
	{
		base.WillEnterForeground(application);
		
		// 应用进入前台时的处理
		// 可以根据需要调整后台任务状态
	}
	
	public override void WillTerminate(UIApplication application)
	{
		base.WillTerminate(application);

		// 应用终止时，停止文件传输前台服务
	
        _foregroundService?.StopService();
	}
}
