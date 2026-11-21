using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.IO;

namespace FileShare.Desktop.Services;

/// <inheritdoc cref="IShellService"/>
public sealed class DesktopShellService : IShellService
{
    private readonly ILogger<DesktopShellService> _logger;

    public DesktopShellService(ILogger<DesktopShellService>? logger = null)
    {
        _logger = logger ?? NullLogger<DesktopShellService>.Instance;
    }

    public void OpenFile(string path) =>
        Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public void OpenUrl(string url) =>
        Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows 路径不允许包含双引号，沿用 explorer 的 /select, 语法
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartWithArguments("open", "-R", path);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            string[] fileManagers = { "nautilus", "dolphin", "thunar", "pcmanfm" };
            foreach (var fileManager in fileManagers)
            {
                try
                {
                    StartWithArguments(fileManager, "--select", path);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "尝试用 {FileManager} 定位文件失败", fileManager);
                }
            }

            try
            {
                var directory = Path.GetDirectoryName(path) ?? path;
                StartWithArguments("xdg-open", directory);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "使用 xdg-open 打开文件目录失败");
                throw new InvalidOperationException("无法打开文件管理器", ex);
            }
        }

        // 其他平台：退化为打开所在目录
        Start(new ProcessStartInfo(Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
    }

    private static void StartWithArguments(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Start(info);
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }
}