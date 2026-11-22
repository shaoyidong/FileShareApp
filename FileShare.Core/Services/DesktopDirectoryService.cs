using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileShare.Core.Services
{
    public partial class DesktopDirectoryService : IPlatformDirectoryService
    {
        // 下载文件夹的GUID标识符[citation:1]
        private static readonly Guid FolderDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int SHGetKnownFolderPath(
           in Guid rfid,
           uint dwFlags,
           IntPtr hToken,
           out string ppszPath
        );

        private string GetDownloadsPathWindows()
        {
            SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out string path);
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
            return GetSpecialFolder(Environment.SpecialFolder.MyDocuments, "Documents");
        }

        public string GetMusicDirectory()
        {
            return GetSpecialFolder(Environment.SpecialFolder.MyMusic, "Music");
        }

        public string GetPicturesDirectory()
        {
            return GetSpecialFolder(Environment.SpecialFolder.MyPictures, "Pictures");
        }

        public string GetVideosDirectory()
        {
            return GetSpecialFolder(Environment.SpecialFolder.MyVideos, "Videos");
        }

        /// <summary>
        /// 读取「我的文档 / 图片 / 音乐 / 视频」这类已知目录，取不到时回退到 <c>~/&lt;名称&gt;</c>。
        /// </summary>
        /// <remarks>
        /// Linux 上这些目录来自 XDG（~/.config/user-dirs.dirs），**没配置过或目录不存在时
        /// GetFolderPath 直接返回空字符串**。空字符串拼出来的保存路径是相对路径，
        /// Directory.CreateDirectory("") 会抛异常，表现为「点接收就失败」——
        /// 手工指定一个存在的目录又能正常接收，现象很迷惑。这里统一兜底成绝对于路径。
        /// </remarks>
        private static string GetSpecialFolder(Environment.SpecialFolder folder, string fallbackName)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                // 极端情况（连主目录都读不到）：退到临时目录，至少保证是可写绝对路径
                home = Path.GetTempPath();
            }

            return Path.Combine(home, fallbackName);
        }
    }
}
