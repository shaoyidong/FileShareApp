using Microsoft.Maui.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public class MauiPickerService : IPickerService
{
    public async Task<FileResult?> PickFileAsync(PickOptions options)
    {
        return await FilePicker.PickAsync(options);
    }

    public async Task<IReadOnlyList<FileResult>> PickFilesAsync(PickOptions options)
    {
        var results = await FilePicker.PickMultipleAsync(options).ConfigureAwait(false);
        return results?.ToList() ?? new List<FileResult>();
    }

    public Task<string?> PickFolderAsync(string title, bool forSaving = false)
    {
#if WINDOWS
        return PickFolderWindowsAsync(title);
#elif ANDROID
        return SafFolderPicker.PickAsync(forSaving);
#else
        // iOS 沙盒不支持目录选择
        return Task.FromResult<string?>(null);
#endif
    }

#if WINDOWS
    private static async Task<string?> PickFolderWindowsAsync(string title)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
        };

        // FolderPicker 需要至少一个过滤项，否则会抛异常
        picker.FileTypeFilter.Add("*");

        var window = Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView
            as Microsoft.UI.Xaml.Window;

        if (window is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
#endif
}