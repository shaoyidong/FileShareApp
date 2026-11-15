namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 历史列表的一行 = 左右两个卡位。
/// <para>VirtualizingStackPanel 只支持单列虚拟化，把两条记录拼成一行既能保留原来的
/// 双列卡片外观，又能让列表走虚拟化（只实例化可视区附近的行）。</para>
/// </summary>
public sealed class HistoryRowViewModel(HistoryItemViewModel? left, HistoryItemViewModel? right)
{
    public HistoryItemViewModel? Left { get; } = left;

    public HistoryItemViewModel? Right { get; } = right;
}
