using System.Threading.Tasks;

namespace FileShare.LocalSend;

/// <summary>外部协议发来的接收请求（尚未写盘）。</summary>
public sealed record ExternalReceiveRequest(
    string SenderId,
    string SenderName,
    string FileName,
    long FileSize,
    string? SaveDirectory);

/// <summary>用户对接收请求的决定。</summary>
public sealed record ExternalReceiveDecision(bool Accept, bool AlwaysAccept)
{
    public static ExternalReceiveDecision AcceptOnce { get; } = new(true, false);
    public static ExternalReceiveDecision Declined { get; } = new(false, false);
}
