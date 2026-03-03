using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileShare.Desktop.Helpers
{
    public static class SqliteLibraryLinker
    {
        // 候选的系统 SQLite 库路径（按可能性排序）
        private static readonly string[] CandidatePaths = new[]
        {
        // Debian/Ubuntu 常见路径（多架构支持）
        "/usr/lib/x86_64-linux-gnu/libsqlite3.so.0",
        "/usr/lib/x86_64-linux-gnu/libsqlite3.so",
        "/usr/lib/aarch64-linux-gnu/libsqlite3.so.0",  // 64位 ARM
        "/usr/lib/arm-linux-gnueabihf/libsqlite3.so.0", // 32位 ARM (hard float)
        "/usr/lib/arm-linux-gnueabi/libsqlite3.so.0",   // 32位 ARM (soft float)
        "/usr/lib/i386-linux-gnu/libsqlite3.so.0",      // 32位 x86
        
        // Red Hat/CentOS/Fedora 常见路径
        "/usr/lib64/libsqlite3.so.0",
        "/usr/lib64/libsqlite3.so",
        "/usr/lib/libsqlite3.so.0",
        "/usr/lib/libsqlite3.so",
        
        // 其他可能位置
        "/lib/x86_64-linux-gnu/libsqlite3.so.0",
        "/lib64/libsqlite3.so.0",
        "/lib/libsqlite3.so.0",
        };

        // 软链接的目标文件名（与 libe_sqlite3.so 相同）
        private const string LINK_NAME = "libe_sqlite3.so";

        /// <summary>
        /// 在程序启动时调用，确保 libe_sqlite3.so 指向系统 SQLite 库
        /// </summary>
        public static void EnsureSystemSqliteLink()
        {
            // 如果当前目录已经存在 libe_sqlite3.so，检查它是否是一个有效的符号链接
            string linkPath = Path.Combine(AppContext.BaseDirectory, LINK_NAME);
            if (File.Exists(linkPath))
            {
                // 如果是符号链接，且指向的库文件存在，就无需处理
                // 如果是普通文件（例如之前从 NuGet 复制的），我们可能需要备份或覆盖
                // 这里简单起见：如果存在且不是链接，则重命名备份
                var fileInfo = new FileInfo(linkPath);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                {
                    // 不是链接，备份原文件
                    File.Move(linkPath, linkPath + ".backup", overwrite: true);
                    Console.WriteLine($"已备份原有 {linkPath} 为 {linkPath}.backup");
                }
                else
                {
                    // 已经是链接，检查指向的目标是否存在
                    var target = GetSymbolicLinkTarget(linkPath);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target))
                    {
                        Console.WriteLine($"软链接已存在且有效，指向 {target}");
                        return;
                    }
                    // 链接无效，删除后重新创建
                    File.Delete(linkPath);
                }
            }

            // 查找系统 SQLite 库
            string systemLibPath = FindSystemSqliteLibrary();
            if (string.IsNullOrEmpty(systemLibPath))
            {
                // 未找到系统库，提示用户安装
                string errorMsg = @"
未在系统中找到 SQLite 共享库 (libsqlite3.so.0)。
SQLite 运行时库是运行本程序所必需的，请使用系统包管理器安装：

   Ubuntu/Debian:
       sudo apt update && sudo apt install libsqlite3-0

   CentOS/RHEL/Fedora:
       sudo yum install sqlite-libs      # 或 sudo dnf install sqlite-libs

   openSUSE:
       sudo zypper install sqlite3

   Arch Linux:
       sudo pacman -S sqlite

安装后请重新运行程序。如果问题仍然存在，可能是系统库路径特殊，请手动查找
libsqlite3.so.0 文件，并在程序启动目录创建符号链接：
   ln -s /找到的路径/libsqlite3.so.0 ./libe_sqlite3.so";
                throw new InvalidOperationException(errorMsg);
            }

            // 创建软链接
            CreateSymbolicLink(linkPath, systemLibPath);
            Console.WriteLine($"已创建软链接 {linkPath} -> {systemLibPath}");
        }

        private static string FindSystemSqliteLibrary()
        {
            foreach (string path in CandidatePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // 额外尝试：使用 `ldconfig -p` 查找（更可靠，但需要解析输出）
            // 这里提供一个简单实现作为备选
            return FindUsingLdconfig();
        }

        private static string FindUsingLdconfig()
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ldconfig",
                        Arguments = "-p",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // 查找 libsqlite3.so 的行，格式通常为 "libsqlite3.so.0 (libc6,x86-64) => /lib/x86_64-linux-gnu/libsqlite3.so.0"
                foreach (string line in output.Split('\n'))
                {
                    if (line.Contains("libsqlite3.so"))
                    {
                        int arrowIndex = line.IndexOf("=>");
                        if (arrowIndex >= 0)
                        {
                            string path = line.Substring(arrowIndex + 2).Trim();
                            if (File.Exists(path))
                                return path;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ldconfig 可能不可用或出错，忽略
            }
            return string.Empty;
        }

        private static string GetSymbolicLinkTarget(string linkPath)
        {
            try
            {
                // 使用 Mono.Posix.NETStandard 包，或者直接使用 Interop
                // 这里使用简化的方法：调用 `readlink` 命令
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "readlink",
                        Arguments = $"\"{linkPath}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string target = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return target;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void CreateSymbolicLink(string linkPath, string targetPath)
        {
            // 删除已存在的文件（如果有）
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            // 调用 `ln -s` 创建软链接
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ln",
                    Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"创建软链接失败，退出码 {process.ExitCode}");
            }
        }
    }
}
