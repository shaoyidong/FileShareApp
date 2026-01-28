using Microsoft.Maui.Storage;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public interface IPickerService
{
    Task<FileResult?> PickFileAsync(PickOptions options);
}
