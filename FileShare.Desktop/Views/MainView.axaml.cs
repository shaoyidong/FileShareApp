using Avalonia.Controls;
using Avalonia.Input;
using FileShare.Desktop.ViewModels;
using System;
using System.Linq;

namespace FileShare.Desktop;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // 支持把文件/文件夹直接拖进窗口发送
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var items = e.DataTransfer?.TryGetFiles();
            if (items is null)
            {
                return;
            }

            var paths = items
                .Select(item => item.Path.LocalPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();

            if (paths.Count > 0)
            {
                await viewModel.HandleDroppedPathsAsync(paths);
            }
        }
        catch (Exception)
        {
            // 拖拽失败不应该影响主流程
        }
    }
}
