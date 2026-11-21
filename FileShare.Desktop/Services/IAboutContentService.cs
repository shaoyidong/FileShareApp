using Avalonia.Media;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>
/// 关于窗口的可变内容服务（内置默认 → 本地 about.json → 远程 about.json）。
/// 通过接口注入，ViewModel 不依赖静态入口；平台/环境相关的通知由注入的
/// <see cref="SynchronizationContext"/> 派发。
/// </summary>
public interface IAboutContentService
{
    AboutContent Current { get; }

    IImage? QrImage { get; }

    event Action? ContentChanged;

    void Initialize(string? dataDirectory = null);

    Task RefreshAsync(bool force = false);
}