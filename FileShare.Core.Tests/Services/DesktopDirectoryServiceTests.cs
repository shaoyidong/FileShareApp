using FileShare.Core.Services;
using System.IO;

namespace FileShare.Core.Tests.Services;

/// <summary>
/// <see cref="DesktopDirectoryService"/> 的路径解析回归测试。
/// <para>重点覆盖 Linux：未配置 XDG（没有 <c>~/.config/user-dirs.dirs</c>）时
/// <c>Environment.GetFolderPath(MyPictures/...)</c> 会返回空字符串，
/// 保存路径随之退化成相对路径，<c>Directory.CreateDirectory("")</c> 直接抛异常，
/// 用户看到的就是「点接收就失败，但手工选一个目录又能收」。
/// 这些断言在 Linux 上跑才有意义（在 WSL 里执行 <c>dotnet test</c> 即可复现）。</para>
/// </summary>
public class DesktopDirectoryServiceTests
{
    private static readonly DesktopDirectoryService Service = new();

    public static TheoryData<string, string> Directories => new()
    {
        { nameof(DesktopDirectoryService.GetPicturesDirectory), Service.GetPicturesDirectory() },
        { nameof(DesktopDirectoryService.GetVideosDirectory), Service.GetVideosDirectory() },
        { nameof(DesktopDirectoryService.GetMusicDirectory), Service.GetMusicDirectory() },
        { nameof(DesktopDirectoryService.GetDocumentsDirectory), Service.GetDocumentsDirectory() },
        { nameof(DesktopDirectoryService.GetDownloadsDirectory), Service.GetDownloadsDirectory() },
    };

    [Theory]
    [MemberData(nameof(Directories))]
    public void SpecialDirectory_IsNotEmpty(string name, string path)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(path),
            $"{name}() 返回了空路径；空路径会让 Directory.CreateDirectory 抛异常，接收文件随之失败");
    }

    [Theory]
    [MemberData(nameof(Directories))]
    public void SpecialDirectory_IsAbsolute(string name, string path)
    {
        Assert.True(
            Path.IsPathRooted(path),
            $"{name}() 返回的不是绝对路径：'{path}'；相对路径会把文件存到进程当前目录");
    }
}
