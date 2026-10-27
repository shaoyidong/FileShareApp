using FileShare.Core.Models;

namespace FileShare.Core.Common;

/// <summary>
/// 文件名 / 相对路径的安全处理。
/// <para>接收端处理的是「对端发来的任意字符串」，必须当作不可信输入：
/// 去掉路径分隔符、控制字符、<c>..</c> 等，防止目录穿越把文件写到意想不到的位置。</para>
/// </summary>
public static class FileNameHelper
{
    /// <summary>单个文件名的最大长度（避免超出文件系统限制）。</summary>
    public const int MaxFileNameLength = 200;

    /// <summary>相对路径的最大长度。</summary>
    public const int MaxRelativePathLength = 512;

    /// <summary>
    /// 清洗为安全的**单段**文件名：不含路径分隔符、非法字符、首尾空白与结尾点号。
    /// </summary>
    /// <param name="fileName">原始文件名（可能来自对端）。</param>
    /// <param name="fallback">无法得到有效名称时使用的兜底名。</param>
    public static string SanitizeFileName(string? fileName, string fallback = "received_file")
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fallback;
        }

        // 只取最后一段，杜绝 "../../etc/passwd" 这类输入
        var name = fileName.Replace('\\', '/');
        var index = name.LastIndexOf('/');
        if (index >= 0)
        {
            name = name[(index + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().Trim();

        // Windows 不允许文件名以点/空格结尾
        result = result.TrimEnd('.', ' ');

        if (result.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(result);
            var stem = Path.GetFileNameWithoutExtension(result);
            if (extension.Length > 16)
            {
                extension = string.Empty;
            }

            var keep = Math.Max(1, MaxFileNameLength - extension.Length);
            if (stem.Length > keep)
            {
                stem = stem[..keep];
            }

            result = stem + extension;
        }

        if (result.Length == 0 || result is "." or "..")
        {
            return fallback;
        }

        return result;
    }

    /// <summary>
    /// 清洗为安全的**相对路径**（可含子目录），用于文件夹传输。
    /// </summary>
    /// <param name="relativePath">对端提供的相对路径，例如 <c>照片/2024/a.jpg</c>。</param>
    /// <returns>规范化后的相对路径；不安全或为空时返回 <c>null</c>。</returns>
    public static string? SanitizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        if (relativePath.Length > MaxRelativePathLength)
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/').Trim();

        // 绝对路径 / 盘符 / UNC 一律拒绝
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var rawSegment in normalized.Split('/'))
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // 目录穿越：直接判定为不安全
                return null;
            }

            segments.Add(SanitizeFileName(segment));
        }

        if (segments.Count == 0)
        {
            return null;
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// 按冲突策略得到实际可写入的文件路径（必要时自动重命名）。
    /// </summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="fileName">已清洗的文件名。</param>
    /// <param name="policy">冲突策略。</param>
    public static string ResolveTargetPath(string directory, string fileName, FileNameConflictPolicy policy)
    {
        var target = Path.Combine(directory, fileName);

        if (policy == FileNameConflictPolicy.Overwrite || !File.Exists(target))
        {
            return target;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 极端情况下退化为带时间戳的名字，保证不会覆盖
        return Path.Combine(directory, $"{stem} ({DateTime.Now:HHmmssfff}){extension}");
    }

    /// <summary>
    /// 判断某个路径是否位于指定根目录之内（用于二次校验，防止穿越）。
    /// </summary>
    public static bool IsUnderDirectory(string rootDirectory, string candidatePath)
    {
        if (string.IsNullOrEmpty(rootDirectory) || string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        var root = Path.GetFullPath(rootDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
