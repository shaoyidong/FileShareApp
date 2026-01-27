#if IOS
using Foundation;
using UIKit;
using System;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public class IosFileTransferForegroundService : IFileTransferForegroundService
{
    private bool _isServiceStarted;
    private nint _backgroundTaskId;

    public void StartService()
    {
        if (!_isServiceStarted)
        {
            // 启动后台任务
            StartBackgroundTask();
            
            _isServiceStarted = true;
        }
    }

    public void StopService()
    {
        if (_isServiceStarted)
        {
            // 取消后台任务
            CancelBackgroundTask();
            
            _isServiceStarted = false;
        }
    }

    private void StartBackgroundTask()
    {
        // 使用 UIApplication 的后台任务 API
        _backgroundTaskId = UIApplication.SharedApplication.BeginBackgroundTask(() => {
            // 后台任务即将过期
            Console.WriteLine("后台任务即将过期");
            CancelBackgroundTask();
        });
        
        // 启动一个长时间运行的任务，保持后台任务活跃
        Task.Run(async () => {
            try
            {
                while (_isServiceStarted)
                {
                    // 定期检查后台任务状态
                    await Task.Delay(3000); // 每3秒检查一次
                    
                    // 如果后台任务已过期，重新启动
                    if (_backgroundTaskId == 0)
                    {
                        StartBackgroundTask();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"后台任务执行异常: {ex.Message}");
            }
        });
    }

    private void CancelBackgroundTask()
    {
        if (_backgroundTaskId != 0)
        {
            UIApplication.SharedApplication.EndBackgroundTask(_backgroundTaskId);
            _backgroundTaskId = 0;
        }
    }

    // 用于外部调用，在文件传输开始时确保后台任务已启动
    public async Task StartServiceAsync()
    {
        if (!_isServiceStarted)
        {
            StartService();
        }
        else if (_backgroundTaskId == 0)
        {
            // 如果后台任务已过期，重新启动
            StartBackgroundTask();
        }
        else
        {
            // 后台任务已启动且未过期，延长后台任务时间
            ExtendBackgroundTask();
        }
    }
    
    private void ExtendBackgroundTask()
    {
        // 取消当前后台任务
        CancelBackgroundTask();
        
        // 启动新的后台任务
        StartBackgroundTask();
    }
}
#endif