using Microsoft.Maui.Storage;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public class MauiPickerService : IPickerService
{
    public async Task<FileResult?> PickFileAsync(PickOptions options)
    {
        return await FilePicker.PickAsync(options);
    }

    // public async Task<IEnumerable<FileResult?>> PickMultipleAsync(PickOptions options)
    // {
    //     return await FilePicker.PickMultipleAsync(options);
    // }

    // public async Task<IEnumerable<FileResult?>> PickMultipleAsync(PickOptions options)
    // {
    //     return await MediaPicker.PickPhotoAsync(options);
    // }
}
