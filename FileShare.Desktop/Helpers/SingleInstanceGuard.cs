using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// 单实例守卫：用命名 Mutex 保证同一用户同一时间只有一个 FileShare 进程。
/// <para>
/// 为什么用 Mutex 而不是「锁文件 / 标记文件」：
/// 进程退出（正常 Shutdown、异常崩溃、任务管理器强杀、注销、断电）时，
/// 操作系统会无条件关闭该进程的所有内核句柄，命名 Mutex 随之自动释放；
/// 不存在「残留锁文件导致再也启动不起来」的状态需要手工清理。
/// 若上一个进程是崩溃退出，Mutex 处于 abandoned 状态——此时抢占方会收到
/// <see cref="AbandonedMutexException"/>，但同时已经拿到所有权，视为可正常启动。
/// </para>
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _name;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    /// <summary>仅测试使用：允许注入自定义名称，避免测试之间互相干扰。</summary>
    internal SingleInstanceGuard(string? name = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? BuildName() : name;
    }

    /// <summary>
    /// 尝试成为唯一实例。返回 true 表示已获得所有权、可以继续启动；
    /// 返回 false 表示已有一个实例在运行，调用方应提示并立即退出。
    /// </summary>
    internal bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_mutex is not null)
        {
            // 已尝试过，重复调用返回首次结果（避免第二次调用误判为自己抢占失败）。
            return _ownsMutex;
        }

        try
        {
            // 经典单实例写法：initiallyOwned:true + createdNew。
            // 不能用 WaitOne(0)：Mutex 对「同一线程」是可重入的，同进程内重复检测会误判为自己抢到。
            // 构造函数在对象「由本次调用创建」时才把所有权交给当前线程，createdNew 精确反映这一点；
            // 名字已存在（别的实例持有）时不会阻塞等待，直接返回。
            Mutex? mutex = null;
            bool acquired;
            try
            {
                mutex = new Mutex(true, _name, out var createdNew);
                acquired = createdNew;
            }
            catch (AbandonedMutexException)
            {
                // 上一实例异常退出：OS/CLR 已把所有权交给我们，这是合法的「可启动」状态。
                // 构造函数抛异常时拿不到实例，重新打开同一命名 Mutex 以便后续能正常释放。
                acquired = true;
                mutex = new Mutex(false, _name);
            }

            if (!acquired && mutex is not null)
            {
                // 未获得所有权：关掉我们这侧的句柄，绝不能在这里 ReleaseMutex。
                mutex.Dispose();
                mutex = null;
            }

            _mutex = mutex;
            _ownsMutex = acquired;
            return acquired;
        }
        catch (Exception)
        {
            // 极端情况下（如受限环境不允许创建命名对象）不要因为「检测机制本身」把应用锁死：
            // 放行启动，保证可用性优先。真正有第二个实例时仍由端口冲突逻辑兜底。
            _mutex = null;
            _ownsMutex = false;
            return true;
        }
    }

    /// <summary>构造跨进程稳定、按用户隔离的 Mutex 名称。</summary>
    private static string BuildName()
    {
        // 不能用 string.GetHashCode：.NET Core 起每次进程启动都会随机化，跨进程值不同。
        // 用 SHA-256 取前 16 位十六进制，稳定且规避用户名里的特殊字符。
        string identity;
        try
        {
            identity = OperatingSystem.IsWindows()
                ? $"{Environment.UserDomainName}\\{Environment.UserName}"
                : Environment.UserName;
        }
        catch (Exception)
        {
            identity = "unknown-user";
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).AsSpan(0, 16).ToString();

        // Windows 上 Local\ 前缀把名字限定在当前会话，避免其他会话/服务干扰；
        // Unix 的命名 Mutex 映射到文件，名字里不能带反斜杠，故不含前缀。
        return OperatingSystem.IsWindows()
            ? $@"Local\FileShare.Desktop.{hash}"
            : $"FileShare.Desktop.{hash}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (Exception)
                {
                    // 进程正在退出；OS 无论如何都会回收，忽略即可。
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }
}
