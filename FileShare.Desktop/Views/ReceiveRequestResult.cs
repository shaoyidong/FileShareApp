namespace FileShare.Desktop.Views;

/// <summary>
/// 接收请求的返回值。
/// </summary>
/// <param name="Accept">是否接收。</param>
/// <param name="AlwaysAccept">是否把对方加入收藏（以后自动接收）。</param>
public sealed record ReceiveRequestResult(bool Accept, bool AlwaysAccept);
