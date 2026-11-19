using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// 重复启动时的「清晰提示」：走平台原生对话框，不依赖 Avalonia 初始化。
/// <para>
/// 关键点：单实例检测发生在 <c>Program.Main</c> 的最前面，此时 Avalonia / IoC / 日志
/// 都还没起来，不能用 MsBox.Avalonia 或 DI 里的 IDialogService。因此这里用
/// Win32 MessageBox（同步阻塞，天然「阻止继续启动」），其他平台降级到系统命令，
/// 任何失败都还有 stderr 兜底。全部为静态 P/Invoke + Process，Native AOT 安全。
/// </para>
/// </summary>
internal static partial class SingleInstanceNotice
{
    private const string Title = "FileShare 局域网传输";
    private const string Message =
        "程序已经在运行了。\n\n" +
        "同一时间只能打开一个 FileShare，本次启动已取消。\n" +
        "请切换到已打开的窗口继续使用（可在任务栏 / 托盘找到它）。";

    /// <summary>弹出提示并等待用户确认后返回；任何异常都不抛出，保证退出路径干净。</summary>
    internal static void Show()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                _ = TryExternal("zenity", "--error", "--title", Title, "--text", Message, "--width", "420")
                    || TryExternal("kdialog", "--error", Title, Message)
                    || TryExternal("notify-send", Title, Message);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // osascript 不接受参数数组里的换行 dialog 文本，用单条 -e 脚本并转义双引号。
                var script = $"display dialog \"{Message.Replace("\"", "\\\"").Replace("\n", "\\n")}\" " +
                             $"buttons {{\"确定\"}} default button \"确定\" with title \"{Title}\" " +
                             "with icon caution";
                TryExternal("osascript", "-e", script);
            }
        }
        catch (Exception)
        {
            // 对话框失败不能影响「阻止第二个实例」这个核心目标。
        }

        // 无论 GUI 提示是否成功，都留一条控制台记录，便于无桌面环境 / 日志排查。
        Console.Error.WriteLine($"{Title}: {Message}");
    }

    private static void ShowWindows()
    {
        // MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND
        const uint flags = 0x00000000u | 0x00000030u | 0x00040000u | 0x00010000u;
        _ = MessageBoxW(IntPtr.Zero, Message, Title, flags);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", SetLastError = false, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>调用外部 GUI 提示命令；命令不存在或启动失败时返回 false。</summary>
    private static bool TryExternal(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            // 同步等待：和 MessageBox 一样，用户关掉提示前第二个实例不会继续往下走。
            process.WaitForExit();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
