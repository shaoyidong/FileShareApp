using System;

namespace LocalSend.Core;

/// <summary>
/// 轻量诊断输出：默认**静默**（GUI 程序不应把协议报文打到标准输出），
/// 设置环境变量 <c>LOCALSEND_VERBOSE=1</c> 后才打印。
/// </summary>
public static class LsLog
{
    private static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("LOCALSEND_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否开启详细日志。</summary>
    public static bool IsVerbose => Verbose;

    /// <summary>输出一行诊断信息（未开启详细日志时什么也不做）。</summary>
    public static void Trace(string message)
    {
        if (Verbose)
        {
            Console.WriteLine(message);
        }
    }
}
