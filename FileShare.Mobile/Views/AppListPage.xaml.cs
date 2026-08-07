using FileShare.Core.Services;
using FileShare.Mobile.Services;
using FileShare.Mobile.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace FileShare.Mobile.Views;

public partial class AppListPage : ContentPage
{
    //private readonly AppListViewModel _viewModel;
    
    public AppListPage(AppListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override async void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        //// 确保Handler已创建后再设置BindingContext
        //if (Handler != null && BindingContext == null)
        //{
        //    BindingContext = Handler.MauiContext?.Services.GetRequiredService<AppListViewModel>();           
        //}
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // 释放资源
        //if (BindingContext is AppListViewModel viewModel)
        //{
        //    viewModel.Dispose();
        //}
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        //数据加载
        if (BindingContext is AppListViewModel viewModel)
        {
            if (viewModel.Apps.Count > 0)
            {
                return;
            }
            Task.Run(async () => await viewModel.LoadAppsAsync());
        }
    }

    // protected override bool OnBackButtonPressed()
    // {
    //     // 处理返回键，返回到MainPage
    //     Shell.Current.GoToAsync("..");
    //     return true;
    // }
}
