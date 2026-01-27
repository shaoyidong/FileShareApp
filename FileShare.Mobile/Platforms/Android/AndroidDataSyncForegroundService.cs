using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Mobile.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.Maui.ApplicationModel.Platform;
using Intent = Android.Content.Intent;

namespace FileShare.Mobile.Platforms.Android;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync)]
public class AndroidDataSyncForegroundService : Service
{
    private const int NOTIFICATION_ID = 1001;
    private const string CHANNEL_ID = "FileTransferServiceChannel";
    private const string CHANNEL_NAME = "文件传输服务";
    private const string CHANNEL_DESCRIPTION = "用于在后台保持文件传输活跃";
    
    private IBinder? _binder;
    private bool _isRunning;
    //private int _transferCount = 0;
    //private List<FileTransferTask> _transferTasks;
    //private IFileShareServiceManager? _serviceManager;

    //private Handler _handler;
    //private Action<FileTransferInfo>? _transferProgressHandler;
    //private Action<FileTransferInfo, string?>? _transferCompletedHandler;



    //public class FileTransferTask
    //{
    //    public string FilePath { get; set; }
    //    public Core.Models.DeviceInfo TargetDevice { get; set; }
    //    public Task<bool> TransferTask { get; set; }
    //}

    public override void OnCreate()
    {
        base.OnCreate();
        // 通过服务提供者获取服务实例
    //    _serviceManager = IPlatformApplication.Current?.Services?
    //.GetService<IFileShareServiceManager>();
        
        _binder = new LocalBinder(this);
        //_transferCount = 0;
        //_transferTasks = new List<FileTransferTask>();
        //_handler = new Handler(Looper.MainLooper);

        //Handler handler = new Handler(Looper.MainLooper!);
        //Initialize(handler);
        
        // 注册传输进度事件
        //if (_serviceManager != null)
        //{
        //    _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
        //    _serviceManager.OnTransferCompleted += OnTransferCompleted;
        //}

#if ANDROID26_0_OR_GREATER
#pragma warning disable CA1416 // 验证平台兼容性
        CreateNotificationChannel();
#pragma warning restore CA1416 // 验证平台兼容性
#endif
     }

    public override IBinder OnBind(Intent? intent)
    {
        return _binder!;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!_isRunning)
        {
            _isRunning = true;
            StartForeground(NOTIFICATION_ID, CreateNotification("文件传输服务", "正在等待传输任务"));
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        
        // 注销事件
        //if (_serviceManager != null)
        //{
        //    _serviceManager.OnTransferProgressUpdated -= OnTransferProgressUpdated;
        //    _serviceManager.OnTransferCompleted -= OnTransferCompleted;           
        //}
        
        _isRunning = false;    
        //_transferCount = 0;
        
        // // 正确停止前台服务并移除通知
        // if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        // {
        //     // Android 13 (API 33) 及以上
        //     StopForeground(StopForegroundFlags.Remove);
        //     _logger?.LogInformation("Foreground service stopped with Remove flag");
        // }
        // else
        // {
        //     // 旧版本Android
        //     StopForeground(true);
        //     _logger?.LogInformation("Foreground service stopped with true flag");
        // }
        //使用兼容api一行解决
        ServiceCompat.StopForeground(this,1);      
    }
    
    private void OnTransferProgressUpdated(FileTransferInfo info)
    {
        try
        {
            UpdateNotification(info);
        }
        catch (Exception)
        {
        }
    }
    
    private void OnTransferCompleted(FileTransferInfo info, string? message)
    {
        try
        {
            UpdateNotification(info);
        }
        catch (Exception)
        {
        }
    }

    //public void Initialize(/*IFileShareServiceManager serviceManager*/ Handler handler)
    //{
    //    //_serviceManager = serviceManager;
    //    if (_transferProgressHandler == null)
    //    {
    //        _transferProgressHandler = (info) =>
    //        {
    //            handler?.Post(() =>
    //            {
    //                UpdateNotification(info);
    //            });
    //        };
    //        _serviceManager?.OnTransferProgressUpdated += _transferProgressHandler;
    //    }

    //    if (_transferCompletedHandler == null)
    //    {
    //        _transferCompletedHandler = (info, message) =>
    //        {
    //            handler?.Post(() =>
    //            {
    //                UpdateNotification(info);
    //                //if (info.Status == TransferStatus.Completed || info.Status == TransferStatus.Failed)
    //                //{
    //                //    RemoveCompletedTask(info.TransferId);
    //                //}
    //            });
    //        };
    //        _serviceManager?.OnTransferCompleted += _transferCompletedHandler;
    //    }
    //}

    //public async Task<bool> SendFileAsync(string filePath, Core.Models.DeviceInfo targetDevice)
    //{        
    //    if (_serviceManager == null)
    //    {
    //        throw new InvalidOperationException("服务管理器未初始化");
    //    }
      
    //    //var transferTask = new FileTransferTask
    //    //{
    //    //    FilePath = filePath,
    //    //    TargetDevice = targetDevice,           
    //    //};

    //    //_transferTasks.Add(transferTask);
    //    _transferCount++;

    //    try
    //    {
    //        //UpdateNotification(null, "正在发送文件...");

    //        //transferTask.TransferTask = _serviceManager.SendFileAsync(filePath, targetDevice);
    //        //var result = await transferTask.TransferTask;
    //        var result = await _serviceManager.SendFileAsync(filePath, targetDevice);
           
    //        //if (_transferTasks.Count == 0)
           

    //        return result;
    //    }
    //    catch (Exception)
    //    {
    //        //_transferTasks.Remove(transferTask);
    //        //if (_transferTasks.Count == 0)
    //        //{
    //        //    StopSelf();
    //        //}           
    //        throw;
    //    }
    //    finally 
    //    {
    //        if (_transferCount > 0)
    //        {
    //            _transferCount--;
    //        }
    //        //if (_transferTasks.Count == 0)
    //        if (_transferCount <= 0)
    //        {
    //            StopSelf();
    //        }
    //    }
    //}

    private void RemoveCompletedTask(string transferId)
    {
        // 这里可以根据transferId移除对应的任务
        // 简化实现，仅在所有任务完成时停止服务
        //if (_transferTasks.TrueForAll(t => t.TransferTask.IsCompleted))
        //{
        //    _transferTasks.Clear();
        //    StopSelf();
        //}
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private void CreateNotificationChannel()
    {
#if ANDROID26_0_OR_GREATER
        // 这段代码只在目标框架为 Android 8.0+ 时编译
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                CHANNEL_ID,
                CHANNEL_NAME,
                NotificationImportance.Default)
            {
                Description = CHANNEL_DESCRIPTION
            };

            // 设置通知渠道的其他属性
            channel.EnableLights(true);
            channel.EnableVibration(false);

            var notificationManager = GetSystemService(NotificationService) as NotificationManager;
            notificationManager?.CreateNotificationChannel(channel);
        }
#endif
    }

    private Notification? CreateNotification(string title, string message)
    {
        Intent intent = new Intent(this, typeof(MainActivity));

        PendingIntent? pendingIntent;

#if ANDROID23_0_OR_GREATER // Android 6.0 (API 23) 及以上
#pragma warning disable CA1416 // 验证平台兼容性
        pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            intent,
            PendingIntentFlags.Immutable
        );
#pragma warning restore CA1416 // 验证平台兼容性

#else
    pendingIntent = PendingIntent.GetActivity(
        this, 
        0, 
        intent, 
        PendingIntentFlags.UpdateCurrent
    );
#endif

        // 使用NotificationCompat确保向后兼容
        var builder = new NotificationCompat.Builder(this, CHANNEL_ID)
            ?.SetContentTitle(title)
            ?.SetContentText(message)
            ?.SetSmallIcon(Resource.Mipmap.appicon)
            ?.SetContentIntent(pendingIntent)
            ?.SetOngoing(true)
            ?.SetPriority(NotificationCompat.PriorityDefault)
            ?.SetVisibility(NotificationCompat.VisibilityPublic)
            ?.SetAutoCancel(false);

        return builder?.Build();
    }

    private void UpdateNotification(FileTransferInfo info, string? defaultMessage = null)
    {
        string title = "文件传输服务";
        string message = defaultMessage ?? "正在传输文件...";

        if (info != null)
        {
            if (info.Status == TransferStatus.Completed)
            {
                message = $"{info.FileName} - 传输完成";
            }
            else if (info.Status == TransferStatus.Failed)
            {
                message = $"{info.FileName} - 传输失败";
            }
            else
            {
                message = $"{info.FileName} - {info.ProgressPercentage}%";
            }
        }

        var notification = CreateNotification(title, message);
        var notificationManager = GetSystemService(NotificationService) as NotificationManager;
        notificationManager?.Notify(NOTIFICATION_ID, notification);
    }
    
    public class LocalBinder : Binder
    {
        public AndroidDataSyncForegroundService Service { get; private set; }

        public LocalBinder(AndroidDataSyncForegroundService service)
        {
            Service = service;
        }
    }
}


