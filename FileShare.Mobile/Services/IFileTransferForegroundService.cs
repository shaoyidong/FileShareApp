using FileShare.Core.Models;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public interface IFileTransferForegroundService
{
    Task<bool> SendFileAsync(string filePath, Core.Models.DeviceInfo targetDevice);
    void StartService();
    void StopService();
}
