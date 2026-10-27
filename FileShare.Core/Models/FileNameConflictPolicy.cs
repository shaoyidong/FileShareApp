namespace FileShare.Core.Models;

/// <summary>
/// 接收文件时的同名文件处理策略。
/// <para>桌面系统上「静默覆盖」是典型的数据丢失来源，因此默认采用自动重命名。</para>
/// </summary>
public enum FileNameConflictPolicy
{
    /// <summary>自动重命名：<c>报告.pdf</c> → <c>报告 (1).pdf</c>（默认）。</summary>
    Rename = 0,

    /// <summary>覆盖同名文件。</summary>
    Overwrite = 1,
}
