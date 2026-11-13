using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Desktop.Services;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 关于窗口的展示数据与交互。
/// <para>
/// 版本号 / 构建日期来自程序集（跟着二进制走）；署名、文案、二维码、链接来自
/// <see cref="AboutContentService"/>，发布后可由本地 about.json 或远程配置覆盖。
/// </para>
/// <para>
/// 内容订阅、后台刷新、打开链接全部收敛在本类，AboutWindow 的 code-behind 只保留
/// 3D 倾斜、关闭、窗口激活等纯视图交互。窗口关闭时调用 <see cref="Dispose"/> 退订静态事件。
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] public partial string Developer { get; private set; }
    [ObservableProperty] public partial string Copyright { get; private set; }
    [ObservableProperty] public partial string Headline { get; private set; }
    [ObservableProperty] public partial string Subtitle { get; private set; }
    [ObservableProperty] public partial string LinkText { get; private set; }

    /// <summary>已校验过的链接地址（只允许 http/https），空串表示没有链接。</summary>
    [ObservableProperty] public partial string LinkUrl { get; private set; }

    [ObservableProperty] public partial IImage? QrImage { get; private set; }

    [ObservableProperty] public partial bool HasHeadline { get; private set; }
    [ObservableProperty] public partial bool HasSubtitle { get; private set; }
    [ObservableProperty] public partial bool HasQrImage { get; private set; }
    [ObservableProperty] public partial bool HasLink { get; private set; }

    /// <summary>可执行程序集的版本号（去掉编译哈希后缀，构造后不再变化）。</summary>
    public string Version { get; }

    /// <summary>构建日期：编译时由 csproj 以 AssemblyMetadata(BuildDate) 打进程序集。</summary>
    public string BuildDate { get; }

    public AboutViewModel()
    {
        Version = ReadVersion();
        BuildDate = ReadBuildDate();

        Developer = string.Empty;
        Copyright = string.Empty;
        Headline = string.Empty;
        Subtitle = string.Empty;
        LinkText = string.Empty;
        LinkUrl = string.Empty;

        Apply(AboutContentService.Current, AboutContentService.QrImage);

        // 远程配置刷新成功后即时重绑；服务会在 UI 线程触发事件
        AboutContentService.ContentChanged += OnContentChanged;

        // 每次构造（即每次打开关于窗口）都尝试拉一次最新配置：
        // 改完远程 JSON 后，用户重新打开关于窗口即可看到新内容。
        // 断网 / 超时全部由服务内部静默处理，这里 fire-and-forget 即可。
        _ = AboutContentService.RefreshAsync(force: true);
    }

    /// <summary>用最新内容刷新全部可绑定属性；enabled=false 时整块动态信息隐藏。</summary>
    public void Apply(AboutContent content, IImage? qrImage)
    {
        Developer = content.Developer ?? string.Empty;
        Copyright = content.Copyright ?? string.Empty;
        Headline = content.Enabled ? content.Headline ?? string.Empty : string.Empty;
        Subtitle = content.Enabled ? content.Subtitle ?? string.Empty : string.Empty;
        LinkText = content.Enabled ? content.LinkText ?? string.Empty : string.Empty;
        LinkUrl = content.Enabled ? SafeHttpUrl(content.LinkUrl) : string.Empty;
        QrImage = content.Enabled ? qrImage : null;

        HasHeadline = !string.IsNullOrWhiteSpace(Headline);
        HasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        HasQrImage = QrImage is not null;
        HasLink = !string.IsNullOrWhiteSpace(LinkText) && !string.IsNullOrWhiteSpace(LinkUrl);
    }

    private void OnContentChanged() =>
        Apply(AboutContentService.Current, AboutContentService.QrImage);

    public void Dispose() =>
        AboutContentService.ContentChanged -= OnContentChanged;

    /// <summary>动态配置里的链接：用系统默认浏览器打开（只允许 http/https）。</summary>
    [RelayCommand]
    private void OpenLink()
    {
        if (string.IsNullOrWhiteSpace(LinkUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(LinkUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 无默认浏览器 / 受限环境：忽略，链接文本本身仍可读
        }
    }

    private static string SafeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        return value.Trim();
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
