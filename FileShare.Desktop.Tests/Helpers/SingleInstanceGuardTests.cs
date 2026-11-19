using System;
using System.Threading;
using FileShare.Desktop.Helpers;

namespace FileShare.Desktop.Tests.Helpers;

/// <summary>
/// 单实例守卫的行为契约：首实例获得、第二实例被拒、释放后可再获得、
/// 异常退出遗留的 abandoned Mutex 能被下一实例接管。
/// 每个用例用独立 GUID 名称，互不干扰，也不影响真实应用。
/// </summary>
public class SingleInstanceGuardTests
{
    private static string UniqueName() => $"FileShare.Tests.SingleInstance.{Guid.NewGuid():N}";

    [Fact]
    public void FirstInstance_AcquiresOwnership()
    {
        using var guard = new SingleInstanceGuard(UniqueName());
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void SecondInstance_IsRejected_WhileFirstAlive()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void AfterFirstDisposed_NewInstanceCanAcquire()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new SingleInstanceGuard(name);
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void RepeatedTryAcquire_ReturnsSameResult()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());
        Assert.True(first.TryAcquire());

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void AbandonedMutex_CanBeTakenOverByNextInstance()
    {
        // 模拟"上一个进程崩溃"：用一个独立线程持有同一名称的原生 Mutex 后直接退出，
        // 且不 ReleaseMutex —— 该 Mutex 变为 abandoned。下一个实例应能接管并继续启动。
        var name = UniqueName();
        var thread = new Thread(() =>
        {
            using var abandoned = new Mutex(false, name);
            abandoned.WaitOne();
            // 线程结束但不释放：等价于进程异常终止，OS/CLR 将其标记为 abandoned。
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        using var next = new SingleInstanceGuard(name);
        Assert.True(next.TryAcquire());
    }
}
