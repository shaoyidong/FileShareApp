using CommunityToolkit.Mvvm.ComponentModel;
using FileShare.Core.Models;

namespace FileShare.Desktop.ViewModels
{
    public partial class FileTransferViewModel : ViewModelBase
    {
        [ObservableProperty]
        /// <summary>
        /// 传输ID
        /// </summary>
        private string _transferId;

        [ObservableProperty]
        /// <summary>
        /// 文件名
        /// </summary>
        private string _fileName;

        private long _fileSize;

        public long FileSize
        {
            get { return _fileSize; }
            set
            {
                if (SetProperty(ref _fileSize,value))
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
                }
            }
        }

        /// <summary>
        /// 发送方ID
        /// </summary>
        public string SenderId { get; set; }

        /// <summary>
        /// 接收方ID
        /// </summary>
        public string ReceiverId { get; set; }

        [ObservableProperty]
        // 传输进度百分比
        private double _progressPercentage;

        [ObservableProperty]
        /// <summary>
        /// 文件保存路径
        /// </summary>
        private string? _savePath;

        // 状态文本显示
        public string StatusText => Status switch
        {
            TransferStatus.Pending => "等待中",
            TransferStatus.Transferring => "传输中",
            TransferStatus.Completed => "已完成",
            TransferStatus.Failed => "失败",
            TransferStatus.Cancelled => "已取消",
            _ => "未知状态"
        };

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
                SavePath = model.SavePath
            };
        }
    }
}