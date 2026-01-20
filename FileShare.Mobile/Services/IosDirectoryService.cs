using FileShare.Core.Services;

namespace FileShare.Mobile.Services;
#if IOS
using Foundation;
public class IosDirectoryService : IPlatformDirectoryService
{
    public string GetDownloadsDirectory()
    {
        return NSFileManager.DefaultManager.GetUrls(
        NSSearchPathDirectory.DownloadsDirectory,
        NSSearchPathDomain.User).FirstOrDefault()?.Path ?? string.Empty;
    }

    public string GetDocumentsDirectory()
    {
        return NSFileManager.DefaultManager.GetUrls(
            NSSearchPathDirectory.DocumentDirectory,
            NSSearchPathDomain.User).FirstOrDefault()?.Path ?? string.Empty;
    }

    public string GetMusicDirectory()
    {
        return NSFileManager.DefaultManager.GetUrls(
            NSSearchPathDirectory.MusicDirectory,
            NSSearchPathDomain.User).FirstOrDefault()?.Path ?? string.Empty;
    }

    public string GetPicturesDirectory()
    {
        return NSFileManager.DefaultManager.GetUrls(
           NSSearchPathDirectory.PicturesDirectory,
           NSSearchPathDomain.User).FirstOrDefault()?.Path ?? string.Empty;
    }

    public string GetVideosDirectory()
    {
        return NSFileManager.DefaultManager.GetUrls(
           NSSearchPathDirectory.MoviesDirectory,
           NSSearchPathDomain.User).FirstOrDefault()?.Path ?? string.Empty;
    }
}
#endif