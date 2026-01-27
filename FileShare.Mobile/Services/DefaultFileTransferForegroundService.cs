using FileShare.Core.Models;
using FileShare.Core.Services;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public class DefaultFileTransferForegroundService : IFileTransferForegroundService
{
    //private IFileShareServiceManager _serviceManager;

    //public DefaultFileTransferForegroundService(IFileShareServiceManager serviceManager)
    //{
    //    _serviceManager = serviceManager;
    //}
    //public async Task<bool> SendFileAsync(string filePath, Core.Models.DeviceInfo targetDevice)
    //{
    //    if (_serviceManager != null)
    //    {
    //        return await _serviceManager.SendFileAsync(filePath, targetDevice);
    //    }

    //    throw new System.InvalidOperationException("服务管理器未初始化");
    //}

    public async Task StartServiceAsync()
    {
        // 在非Android平台，不需要启动前台服务
    }

    public void StopService()
    {
        // 在非Android平台，不需要停止前台服务
    }
}
