namespace FileShare.Core.Models;

/// <summary>
/// 发送队列控制器：支持「暂停 / 继续 / 取消」一批文件（在文件之间生效，当前文件不会被中断）。
/// <para>用于「发送文件夹」这类长任务：用户可以先暂停，等对端准备好再继续。</para>
/// </summary>
public sealed class TransferQueueControl : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _paused;

    /// <summary>本队列的取消令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>是否处于暂停状态。</summary>
    public bool IsPaused => _paused;

    /// <summary>是否已取消。</summary>
    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>暂停（当前文件传完后停在下一个文件之前）。</summary>
    public void Pause() => _paused = true;

    /// <summary>继续。</summary>
    public void Resume() => _paused = false;

    /// <summary>取消整批发送。</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    /// <summary>
    /// 若处于暂停状态则等待，直到继续或取消。
    /// </summary>
    internal async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (_paused && !_cts.IsCancellationRequested)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        _cts.Token.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();
}
