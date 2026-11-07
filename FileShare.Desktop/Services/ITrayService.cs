using System;
using Avalonia.Controls;

namespace FileShare.Desktop.Services;

/// <summary>
/// 系统托盘：常驻图标 + 菜单（显示主窗口 / 退出），配合「最小化到托盘」「关闭到托盘」使用。
/// </summary>
public interface ITrayService : IDisposable
{
    /// <summary>托盘是否可用（图标创建成功）。</summary>
    bool IsAvailable { get; }

    /// <summary>把托盘图标挂到窗口上（每个窗口只需调用一次）。</summary>
    void Attach(Window window);

    /// <summary>显示/隐藏托盘图标。</summary>
    void SetVisible(bool visible);

    /// <summary>从托盘恢复主窗口。</summary>
    void ShowMainWindow();

    /// <summary>隐藏主窗口到托盘。</summary>
    void HideMainWindow();

    /// <summary>是否正在退出（用于让主窗口正确响应关闭）。</summary>
    bool IsExiting { get; set; }

    /// <summary>退出应用。</summary>
    void Exit();
}
