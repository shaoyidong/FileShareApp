using FileShare.Core.Services;

namespace FileShare.Mobile.Services;
#if IOS
using Foundation;
    public class IosDirectoryService : IPlatformDirectoryService
    {
        public string GetDownloadsDirectory()
        {
            var paths = NSSearchPath.GetDirectories(
                NSSearchPathDirectory.DownloadsDirectory,
                NSSearchPathDomain.User);
            return paths.Length > 0 ? paths[0] :
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        public string GetDocumentsDirectory()
        {
            return NSFileManager.DefaultManager.GetUrls(
                NSSearchPathDirectory.DocumentDirectory,
                NSSearchPathDomain.User)[0]?.Path;
        }        

        public string GetMusicDirectory()
        {
            return NSFileManager.DefaultManager.GetUrls(
                NSSearchPathDirectory.MusicDirectory,
                NSSearchPathDomain.User)[0]?.Path;
        }

        public string GetPicturesDirectory()
        {
            return NSFileManager.DefaultManager.GetUrls(
               NSSearchPathDirectory.PicturesDirectory,
               NSSearchPathDomain.User)[0]?.Path;
        }

        public string GetVideosDirectory()
        {
            return NSFileManager.DefaultManager.GetUrls(
               NSSearchPathDirectory.MoviesDirectory,
               NSSearchPathDomain.User)[0]?.Path;
        }
    }
#endif