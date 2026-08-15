using Avalonia.Controls;
using Avalonia.Controls.Templates;
using FileShare.Desktop.ViewModels;
using FileShare.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics.CodeAnalysis;

namespace FileShare.Desktop
{
    /// <summary>
    /// Given a view model, returns the corresponding view if possible.
    /// </summary>
    [RequiresUnreferencedCode(
        "Default implementation of ViewLocator involves reflection which may be trimmed away.",
        Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
    public class ViewLocator : IDataTemplate
    {
        private readonly IServiceProvider _serviceProvider;

        public ViewLocator(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Control? Build(object? param)
        {
            Control view = param switch
            {
                MainViewModel => _serviceProvider.GetRequiredService<MainView>(),
                HistoryViewModel => _serviceProvider.GetRequiredService<HistoryView>(),
                _ => new TextBlock { Text = $"No view for {param?.GetType().Name}" }
            };
            // 关键：将 ViewModel 设置到视图的 DataContext
            if (view is Control c && param is not null)
                c.DataContext = param;
            return view;
        }

        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
    }
}
