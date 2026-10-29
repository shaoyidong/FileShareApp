using Microsoft.Maui.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

public interface IPickerService
{
    Task<FileResult?> PickFileAsync(PickOptions options);

    /// <summary>一次选择多个文件。</summary>
    Task<IReadOnlyList<FileResult>> PickFilesAsync(PickOptions options);

    /// <summary>
    /// 选择文件夹，返回本地绝对路径；平台不支持时返回 <c>null</c>。
    /// <para>Android / iOS 受 SAF 限制，需要通过系统目录选择器授权后拷贝，暂未接入。</para>
    /// </summary>
    Task<string?> PickFolderAsync(string title, bool forSaving = false);
}