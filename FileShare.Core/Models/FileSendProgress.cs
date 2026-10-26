namespace FileShare.Core.Models;

/// <summary>
/// 多文件发送进度（一队文件逐个发送，每完成一个报告一次）。
/// </summary>
/// <param name="Index">已完成的数量（从 1 开始）。</param>
/// <param name="Total">本批次文件总数。</param>
/// <param name="FilePath">刚处理完的文件。</param>
/// <param name="Success">该文件是否发送成功。</param>
public sealed record FileSendProgress(int Index, int Total, string FilePath, bool Success)
{
    /// <summary>完成百分比（0~100）。</summary>
    public double Percent => Total <= 0 ? 100d : Math.Round((double)Index / Total * 100d, 1);
}
