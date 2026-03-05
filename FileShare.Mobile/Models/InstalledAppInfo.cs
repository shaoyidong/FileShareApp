using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Mobile.Models
{
    /// <summary>
    /// 已安装应用信息
    /// </summary>
    public class InstalledAppInfo
    {
        /// <summary>
        /// 应用包名
        /// </summary>
        public string PackageName { get; set; } = string.Empty;

        /// <summary>
        /// 应用名称
        /// </summary>
        public string AppName { get; set; } = string.Empty;

        /// <summary>
        /// 应用版本
        /// </summary>
        public string VersionName { get; set; } = string.Empty;

        /// <summary>
        /// 应用图标路径
        /// </summary>
        public ImageSource? Icon { get; set; }

        /// <summary>
        /// APK文件大小
        /// </summary>
        public long ApkSize { get; set; }

        /// <summary>
        /// 是否为系统应用
        /// </summary>
        public bool IsSystemApp { get; set; }

        public string FormattedApkSize => FormatFileSize(ApkSize);

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
    }
}
