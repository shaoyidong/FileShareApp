namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 接收请求的返回值（由 <see cref="ReceiveRequestViewModel"/> 产出，弹窗调用方消费）。
/// </summary>
/// <param name="Accept">是否接收。</param>
/// <param name="AlwaysAccept">是否把对方加入收藏（以后自动接收）。</param>
public sealed record ReceiveRequestResult(bool Accept, bool AlwaysAccept);
