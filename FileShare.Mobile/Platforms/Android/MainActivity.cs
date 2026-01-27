using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using FileShare.Mobile.Services;
using System.Runtime.Versioning;

namespace FileShare.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private IFileTransferForegroundService? _fileTransferService;
    private const int NOTIFICATION_PERMISSION_REQUEST_CODE = 100;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 请求通知权限
#pragma warning disable CA1416 // 验证平台兼容性
        RequestNotificationPermission();
#pragma warning restore CA1416 // 验证平台兼容性

        // 初始化文件传输服务
        InitializeFileTransferService();
    }

    protected override void OnResume()
    {
        base.OnResume();       
        // 应用恢复时确保服务运行
        //if (_fileTransferService != null)
        //{
        //    _fileTransferService.StartService();
        //}
    }

    protected override void OnPause()
    {     
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();        
        // 应用销毁时停止服务
        _fileTransferService?.StopService();
        
    }

    private void InitializeFileTransferService()
    {
        // 通过服务提供者获取服务实例
        _fileTransferService = IPlatformApplication.Current?.Services?
    .GetService<IFileTransferForegroundService>();
    }

    [SupportedOSPlatform("android33.0")]
    private void RequestNotificationPermission()
    {
        // Android 13 (API 33) 及以上需要请求通知权限
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (ContextCompat.CheckSelfPermission(this, Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(
                    this,
                    new[] { Android.Manifest.Permission.PostNotifications },
                    NOTIFICATION_PERMISSION_REQUEST_CODE
                );
            }
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
#if ANDROID23_0_OR_GREATER
#pragma warning disable CA1416 // 验证平台兼容性
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
#pragma warning restore CA1416 // 验证平台兼容性
#endif

        if (requestCode == NOTIFICATION_PERMISSION_REQUEST_CODE)
        {
            if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
            {
                // 通知权限已授予，确保服务运行
                /*if (_fileTransferService != null)
                {
                    _fileTransferService.StartService();
                }*/
            }
            else
            {
                // 通知权限被拒绝，可能需要向用户解释为什么需要这个权限
                // 但前台服务仍然会运行，只是不会显示通知
            }
        }
    }
}
