namespace FileShare.Desktop.Services;

/// <summary>
/// 系统外壳服务：打开文件 / URL、在文件管理器中定位文件。
/// 把 Windows/macOS/Linux 的进程启动差异收敛在一个实现里，ViewModel 不出现 OS 分支。
/// </summary>
public interface IShellService
{
    void OpenFile(string path);

    void OpenUrl(string url);

    void RevealInFileManager(string path);
}

/// <summary>设计器 / 单元测试使用的空实现。</summary>
internal sealed class NullShellService : IShellService
{
    public static readonly NullShellService Instance = new();

    public void OpenFile(string path)
    {
    }

    public void OpenUrl(string url)
    {
    }

    public void RevealInFileManager(string path)
    {
    }
}