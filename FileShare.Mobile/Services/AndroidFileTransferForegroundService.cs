#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Mobile.Platforms.Android;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public class AndroidFileTransferForegroundService : IFileTransferForegroundService
{
    private AndroidDataSyncForegroundService? FileTransferService { get; set; }
    //private IFileShareServiceManager ServiceManager { get; set; }
    private bool IsBound { get; set; }
    private bool _isServiceStarted;
    private ServiceConnection _serviceConnection;

    public AndroidFileTransferForegroundService(/*IFileShareServiceManager serviceManager*/)
    {
        //ServiceManager = serviceManager;
        _serviceConnection = new ServiceConnection(this);
        
    }

    //public async Task<bool> SendFileAsync(string filePath, Core.Models.DeviceInfo targetDevice)
    //{
    //    if (!_isServiceStarted)
    //    {
    //        await StartServiceAsync();
    //    }
    //    if (!IsBound || FileTransferService == null)
    //    {
    //        // 如果服务未绑定，先绑定服务
    //        await BindServiceAsync();
    //    }

    //    if (FileTransferService != null)
    //    {
    //        return await FileTransferService.SendFileAsync(filePath, targetDevice);
    //    }

    //    throw new System.InvalidOperationException("文件传输服务未初始化");
    //}

    public async Task StartServiceAsync()
    {
        if (!_isServiceStarted)
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(AndroidDataSyncForegroundService));
            // 添加API级别判断
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) // Android 8.0 或更高
            {
#pragma warning disable CA1416 // 验证平台兼容性
                context.StartForegroundService(intent);
#pragma warning restore CA1416 // 验证平台兼容性
            }
            else
            {
                // 对于 Android 8.0 以下的系统，使用旧版的 StartService 方法
                context.StartService(intent);
            }
            if (!IsBound || FileTransferService == null)
            {
                // 如果服务未绑定，先绑定服务
                await  BindServiceAsync();
            }

            _isServiceStarted = true;
        }
    }

    public void StopService()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidDataSyncForegroundService));
        context.StopService(intent);
        _isServiceStarted = false;
        
        if (IsBound && _serviceConnection != null)
        {
            context.UnbindService(_serviceConnection);
            IsBound = false;
            FileTransferService = null;
        }
    }

    private async Task BindServiceAsync()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidDataSyncForegroundService));       
       
        IsBound = context.BindService(intent, _serviceConnection, Bind.AutoCreate);
        
        // 等待服务绑定完成
        await Task.Delay(100);
    }

    private class ServiceConnection : Java.Lang.Object, IServiceConnection
    {
        private AndroidFileTransferForegroundService _service;

        public ServiceConnection(AndroidFileTransferForegroundService service)
        {
            _service = service;
        }

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            var binder = service as AndroidDataSyncForegroundService.LocalBinder;
            if (binder != null)
            {
                _service.FileTransferService = binder.Service;
                //if (_service.ServiceManager != null)
                //{
                //    _service.FileTransferService.Initialize(_service.ServiceManager);
                //}
            }
        }

        public void OnServiceDisconnected(ComponentName? name)
        {
            _service.FileTransferService = null;
            _service.IsBound = false;
        }
    }
}
#endif