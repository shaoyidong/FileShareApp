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
public class FileTransferService : Service
{
    private const int NOTIFICATION_ID = 1001;
    private const string CHANNEL_ID = "FileTransferServiceChannel";
    
    private IBinder? _binder;
    private bool _isRunning;
    private int _transferCount = 0;
    //private List<FileTransferTask> _transferTasks;
    private IFileShareServiceManager? _serviceManager;
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
        _serviceManager = IPlatformApplication.Current?.Services?
    .GetService<IFileShareServiceManager>();
        _binder = new LocalBinder(this);
        _transferCount = 0;
        //_transferTasks = new List<FileTransferTask>();
        //_handler = new Handler(Looper.MainLooper);

        //Handler handler = new Handler(Looper.MainLooper!);
        //Initialize(handler);

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
        _isRunning = false;    
        _transferCount = 0;
        //_transferTasks.Clear();
//        // 对于 Android 13 (API 33) 及以上
//        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
//        {
//            // 移除通知
//            StopForeground(StopForegroundFlags.Remove);
//            // 或者，如果想让通知暂时保留：StopForeground(StopForegroundFlags.Detach);
//        }
//        else
//        {
//            // 为了兼容旧版本，保留原来的调用
//#if ANDROID33_0_OR_GREATER
//            StopForeground(StopForegroundFlags.Remove);
//#else
//            StopForeground(true);
//#endif

//        }
        // 使用兼容性 API，一行代码解决问题
        ServiceCompat.StopForeground(this,1);
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

    public async Task<bool> SendFileAsync(string filePath, Core.Models.DeviceInfo targetDevice)
    {
        if (_serviceManager == null)
        {
            throw new InvalidOperationException("服务管理器未初始化");
        }
     
        //var transferTask = new FileTransferTask
        //{
        //    FilePath = filePath,
        //    TargetDevice = targetDevice,           
        //};

        //_transferTasks.Add(transferTask);
        _transferCount++;

        try
        {
            //UpdateNotification(null, "正在发送文件...");

            //transferTask.TransferTask = _serviceManager.SendFileAsync(filePath, targetDevice);
            //var result = await transferTask.TransferTask;
            var result = await _serviceManager.SendFileAsync(filePath, targetDevice);
           
            //if (_transferTasks.Count == 0)
           

            return result;
        }
        catch (Exception)
        {
            //_transferTasks.Remove(transferTask);
            //if (_transferTasks.Count == 0)
            //{
            //    StopSelf();
            //}           
            throw;
        }
        finally 
        {
            if (_transferCount > 0)
            {
                _transferCount--;
            }
            //if (_transferTasks.Count == 0)
            if (_transferCount <= 0)
            {
                StopSelf();
            }
        }
    }

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
                "文件传输服务",
                NotificationImportance.Default)
            {
                Description = "用于在后台保持文件传输活跃"
            };

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

        var builder = new NotificationCompat.Builder(this, CHANNEL_ID)
            ?.SetContentTitle(title)
            ?.SetContentText(message)
            ?.SetSmallIcon(Resource.Mipmap.appicon)
            ?.SetContentIntent(pendingIntent)
            ?.SetOngoing(true)
            ?.SetPriority(NotificationCompat.PriorityDefault);

        return builder?.Build();
    }

    private void UpdateNotification(FileTransferInfo info, string? defaultMessage = null)
    {
        string title = "文件传输服务";
        string message = defaultMessage ?? "正在传输文件...";

        if (info != null)
        {
            message = $"{info.FileName} - {info.ProgressPercentage}%";
            if (info.Status == TransferStatus.Completed)
            {
                message = $"{info.FileName} - 传输完成";
            }
            else if (info.Status == TransferStatus.Failed)
            {
                message = $"{info.FileName} - 传输失败";
            }
        }

        var notification = CreateNotification(title, message);
        var notificationManager = GetSystemService(NotificationService) as NotificationManager;
        notificationManager?.Notify(NOTIFICATION_ID, notification);
    }
    public class LocalBinder : Binder
    {
        public FileTransferService Service { get; private set; }

        public LocalBinder(FileTransferService service)
        {
            Service = service;
        }
    }
}


