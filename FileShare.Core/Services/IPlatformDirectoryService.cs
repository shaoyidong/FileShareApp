using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Services
{
    public interface IPlatformDirectoryService
    {
        string GetDownloadsDirectory();
        string GetPicturesDirectory();
        string GetVideosDirectory();
        string GetMusicDirectory();
        string GetDocumentsDirectory();
        // ... 可以根据需要添加更多目录
    }
}
