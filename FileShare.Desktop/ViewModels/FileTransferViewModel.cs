using CommunityToolkit.Mvvm.ComponentModel;
using FileShare.Core.Models;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.ViewModels
{
    public partial class FileTransferViewModel : ViewModelBase
    {
        /// <summary>
        /// 传输ID
        /// </summary>
        [ObservableProperty]
        private string? _transferId;

        /// <summary>
        /// 文件名
        /// </summary>
        [ObservableProperty]
        private string? _fileName;

        private long _fileSize;

        public long FileSize
        {
            get { return _fileSize; }
            set
            {
                if (SetProperty(ref _fileSize, value))
                {
                    OnPropertyChanged(nameof(FormattedFileSize));
                }
            }
        }

        private long _transferredSize;

        /// <summary>
        /// 已传输大小（字节）
        /// </summary>
        public long TransferredSize
        {
            get { return _transferredSize; }
            set
            {
                if (SetProperty(ref _transferredSize, value))
                {
                    OnPropertyChanged(nameof(FormattedTransferredSize));
                }
            }
        }

        // 格式化的文件大小显示（如1.2MB）
        public string FormattedFileSize => FormatFileSize(_fileSize);

        // 格式化的已传输大小显示
        public string FormattedTransferredSize => FormatFileSize(_transferredSize);

        private long _speedBytesPerSecond;

        /// <summary>
        /// 瞬时速率（字节/秒）。
        /// </summary>
        public long SpeedBytesPerSecond
        {
            get => _speedBytesPerSecond;
            set
            {
                if (SetProperty(ref _speedBytesPerSecond, value))
                {
                    OnPropertyChanged(nameof(SpeedText));
                    OnPropertyChanged(nameof(ShowSpeed));
                }
            }
        }

        /// <summary>速率文本，如 "12.3MB/s"。</summary>
        public string SpeedText => $"{FormatFileSize(_speedBytesPerSecond)}/s";

        /// <summary>是否显示速率（仅在传输中且有采样时显示）。</summary>
        public bool ShowSpeed => _speedBytesPerSecond > 0 && Status == TransferStatus.Transferring;

        /// <summary>
        /// 传输状态
        /// </summary>
        private TransferStatus _status;

        public TransferStatus Status
        {
            get { return _status; }
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(ShowAcceptReject));
                    OnPropertyChanged(nameof(ShowSpeed));
                }
            }
        }

        /// <summary>
        /// 发送方ID
        /// </summary>
        public string? SenderId { get; set; }

        /// <summary>本机源文件路径（仅发送方向有值，用于「重试」）。</summary>
        public string? SourcePath { get; set; }

        /// <summary>目标设备 ID（仅发送方向有值）。</summary>
        public string? TargetDeviceId { get; set; }

        /// <summary>是否允许「重试」（发送失败且知道源文件时）。</summary>
        public bool CanRetry => Direction == TransferDirection.Send
                                && Status == TransferStatus.Failed
                                && !string.IsNullOrEmpty(SourcePath)
                                && !string.IsNullOrEmpty(TargetDeviceId);

        /// <summary>
        /// 接收方ID
        /// </summary>
        public string? ReceiverId { get; set; }

        [ObservableProperty]
        // 传输进度百分比
        private double _progressPercentage;

        /// <summary>
        /// 文件保存路径
        /// </summary>
        [ObservableProperty]
        private string? _savePath;

        // 状态文本显示
        public string StatusText => Status switch
        {
            TransferStatus.Pending => Loc.Get("等待中"),
            TransferStatus.Transferring => Loc.Get("传输中"),
            TransferStatus.Completed => Loc.Get("已完成"),
            TransferStatus.Failed => Loc.Get("失败"),
            TransferStatus.Cancelled => Loc.Get("已取消"),
            _ => Loc.Get("未知状态")
        };

        private TransferDirection _direction;
        public TransferDirection Direction
        {
            get { return _direction; }
            set
            {
                if (SetProperty(ref _direction, value))
                {
                    OnPropertyChanged(nameof(DirectionText));
                    OnPropertyChanged(nameof(ShowAcceptReject));
                }
            }
        }

        public string DirectionText => Direction switch
        {
            TransferDirection.Send => Loc.Get("发送"),
            TransferDirection.Receive => Loc.Get("接收"),
            _ => Loc.Get("未知状态")
        };

        // 只有在接收且处于 Pending 时显示接受/拒绝
        public bool ShowAcceptReject => Direction == TransferDirection.Receive && Status == TransferStatus.Pending;

        // 格式化文件大小的辅助方法
        private string FormatFileSize(long size)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            double sizeDouble = size;

            while (sizeDouble >= 1024 && index < suffixes.Length - 1)
            {
                sizeDouble /= 1024;
                index++;
            }

            return $"{sizeDouble:F1}{suffixes[index]}";
        }

        public static FileTransferViewModel FromModel(FileTransferInfo model)
        {
            return new FileTransferViewModel
            {
                TransferId = model.TransferId,
                FileName = model.FileName,
                FileSize = model.FileSize,
                TransferredSize = model.TransferredSize,
                Status = model.Status,
                SenderId = model.SenderId,
                ReceiverId = model.ReceiverId,
                ProgressPercentage = model.ProgressPercentage,
                SavePath = model.SavePath,
                Direction = model.Direction,
                SpeedBytesPerSecond = model.TransferRateBytesPerSec,
                SourcePath = model.Direction == TransferDirection.Send ? model.FilePath : null,
                TargetDeviceId = model.Direction == TransferDirection.Send ? model.ReceiverId : null
            };
        }
    }
}
