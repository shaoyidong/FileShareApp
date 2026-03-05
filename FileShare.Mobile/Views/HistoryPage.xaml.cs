using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using FileShare.Mobile.ViewModels;

namespace FileShare.Mobile.Views;

public partial class HistoryPage : ContentPage
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        this.Loaded += HistoryPage_Loaded;
    }

    private void HistoryPage_Loaded(object? sender, EventArgs e)
    {
        //数据加载
        if (BindingContext is HistoryViewModel viewModel)
        {
            Task.Run(async () => await viewModel.LoadHistoryAsync());
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();       
    }
    
    // 获取元素绝对位置的辅助方法（你需要自己实现）
    private Point GetAbsolutePosition(VisualElement element)
    {
        // 方法一：使用 Microsoft.Maui.Controls.ElementExtensions
        // var bounds = element.GetAbsoluteBounds(); // 这个扩展方法可能不存在，取决于版本

        // 方法二：手动递归计算（最保险）
        double x = 0, y = 0;
        var currentElement = element;
        while (currentElement != null)
        {
            x += currentElement.X;
            y += currentElement.Y;
            currentElement = currentElement.Parent as VisualElement;
        }

        // 注意：这样计算得到的是相对于页面的坐标，不是屏幕坐标。
        // 如果页面有滚动或偏移，可能需要额外处理。
        // 一个更可靠的方式是使用 IView 接口的 GetLocationOnScreen 方法（需要自定义渲染器或原生平台调用）。

        return new Point(x, y);
    }
}
