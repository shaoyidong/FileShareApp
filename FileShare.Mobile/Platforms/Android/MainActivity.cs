using Android.App;
using Android.Content.PM;
using Android.OS;
using FileShare.Mobile.Services;

namespace FileShare.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private IFileTransferForegroundService? _fileTransferService;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        
        // 初始化文件传输服务
        InitializeFileTransferService();
    }

    protected override void OnResume()
    {
        base.OnResume();
        
        // 应用恢复时确保服务运行
        if (_fileTransferService != null)
        {
            _fileTransferService.StartService();
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        // 应用暂停时不停止服务，让服务继续运行
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        
        // 应用销毁时停止服务
        if (_fileTransferService != null)
        {
            _fileTransferService.StopService();
        }
    }

    private void InitializeFileTransferService()
    {
        // 通过服务提供者获取服务实例
        _fileTransferService = IPlatformApplication.Current?.Services?
    .GetService<IFileTransferForegroundService>();
    }
}
