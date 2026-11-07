using System;

namespace FileShare.Desktop.Services;

/// <summary>
/// 轻量「系统通知」：在工作区右下角弹出一个自绘小卡片，几秒后自动消失，点击可回到主窗口。
/// <para>不依赖平台 Toast API（Windows/macOS/Linux 差异大且部分环境不可用），
/// 因此跨平台行为一致，并且能继承当前皮肤。</para>
/// </summary>
public interface IToastService
{
    /// <summary>显示一条通知（新的会替换旧的）。</summary>
    void Show(string title, string message, Action? onClick = null);
}
