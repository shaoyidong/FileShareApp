using FileShare.Core.Services;
namespace FileShare.Mobile.Services;
#if ANDROID
using Environment = Android.OS.Environment;
public class AndroidDirectoryService : IPlatformDirectoryService
{
    public string GetDownloadsDirectory()
    {
        return Environment.GetExternalStoragePublicDirectory(
                Environment.DirectoryDownloads)?.AbsolutePath;
    }

    public string GetPicturesDirectory()
    {
        return Environment.GetExternalStoragePublicDirectory(
                Environment.DirectoryPictures)?.AbsolutePath;
    }

    public string GetVideosDirectory()
    {
        return Environment.GetExternalStoragePublicDirectory(
                Environment.DirectoryMovies)?.AbsolutePath;
    }

    public string GetMusicDirectory()
    {
        return Environment.GetExternalStoragePublicDirectory(
                 Environment.DirectoryMusic)?.AbsolutePath;
    }

    public string GetDocumentsDirectory()
    {
        return Environment.GetExternalStoragePublicDirectory(
                Environment.DirectoryDocuments)?.AbsolutePath;
    }
}
#endif