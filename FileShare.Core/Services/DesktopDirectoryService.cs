using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileShare.Core.Services
{
    public class DesktopDirectoryService : IPlatformDirectoryService
    {
        // 下载文件夹的GUID标识符[citation:1]
        private static readonly Guid FolderDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
           [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
           uint dwFlags,
           IntPtr hToken,
           out string ppszPath
        );

        private string GetDownloadsPathWindows()
        {
            string path;
            SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out path);
            return path;
        }

        // 一个综合的跨平台尝试方法
        private string GetDownloadsPathCrossPlatform()
        {
            // 优先尝试Windows API
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    return GetDownloadsPathWindows();
                }
                catch
                {
                    // API调用失败，回退到备用方案
                }
            }

            // 备用方案：基于用户主目录拼接
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultDownloads = Path.Combine(userProfile, "Downloads");

            // (可选) 可以在此添加macOS或Linux的特定逻辑
            // 例如，读取环境变量或配置文件

            return defaultDownloads;
        }

        public string GetDownloadsDirectory()
        {
            return GetDownloadsPathCrossPlatform();
        }

        public string GetDocumentsDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }       

        public string GetMusicDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }

        public string GetPicturesDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        public string GetVideosDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
    }
}
