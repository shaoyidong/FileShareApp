namespace FileShare.Desktop.Tests;

/// <summary>
/// 测试用的同步 UI 上下文：把 Post 的回调立即在当前线程执行。
/// <para>
/// MainViewModel.RunOnUiThread 用 Post 投递（避免后台线程阻塞、避免 UI 线程被带外插队），
/// 而默认 <see cref="System.Threading.SynchronizationContext"/> 的 Post 会丢到线程池异步执行，
/// 导致「触发事件 → 立刻断言」的测试出现竞态。
/// 用同步替身让测试保持确定性，同时不改动产品里的异步投递行为。
/// </para>
/// </summary>
public sealed class ImmediateSynchronizationContext : System.Threading.SynchronizationContext
{
    public override void Post(System.Threading.SendOrPostCallback d, object? state) => d(state);

    public override void Send(System.Threading.SendOrPostCallback d, object? state) => d(state);
}
