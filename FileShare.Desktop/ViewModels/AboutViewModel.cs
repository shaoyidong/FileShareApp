using System;
using System.Linq;
using System.Reflection;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 关于窗口的展示数据：版本号 / 构建日期 / 署名。取值在构造时读定，窗口存续期内不变。
/// 倾斜等纯视图交互留在 AboutWindow 的 code-behind。
/// </summary>
public class AboutViewModel : ViewModelBase
{
    /// <summary>可执行程序集的版本号（去掉编译哈希后缀）。</summary>
    public string Version { get; }

    /// <summary>构建日期：编译时由 csproj 以 AssemblyMetadata(BuildDate) 打进程序集。</summary>
    public string BuildDate { get; }

    public string Developer { get; } = "Developed by yidong.shao";

    public string Copyright { get; } = "Copyright © 2026 yidong.shao";

    public AboutViewModel()
    {
        Version = ReadVersion();
        BuildDate = ReadBuildDate();
    }

    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return "-";
        }

        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    private static string ReadBuildDate()
    {
        var date = Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildDate", StringComparison.Ordinal))?.Value;
        return string.IsNullOrEmpty(date) ? "-" : date!;
    }
}
