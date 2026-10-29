#if ANDROID
using Android.App;
using Android.Content;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using System;
using System.IO;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

/// <summary>
/// Android 目录选择（SAF，<c>ACTION_OPEN_DOCUMENT_TREE</c>）。
/// <para>
/// 结果优先解析为真实文件系统路径（<c>primary:</c> 存储 + MANAGE_EXTERNAL_STORAGE 时可用）；
/// 解析不到时，为了「发送文件夹」可用，会把整棵树拷到应用缓存目录再返回；
/// 而「保存位置」必须能写真实路径，解析不到则返回 <c>null</c> 由界面提示。
/// </para>
/// </summary>
internal static class SafFolderPicker
{
    /// <summary>与 <c>MainActivity.OnActivityResult</c> 约定好的请求码。</summary>
    public const int RequestCode = 0x5AF0;

    private static TaskCompletionSource<string?>? _pending;
    private static bool _forSaving;

    public static Task<string?> PickAsync(bool forSaving)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            return Task.FromResult<string?>(null);
        }

        _forSaving = forSaving;
        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission
                            | ActivityFlags.GrantWriteUriPermission
                            | ActivityFlags.GrantPersistableUriPermission
                            | ActivityFlags.GrantPrefixUriPermission);
            activity.StartActivityForResult(intent, RequestCode);
        }
        catch (Exception)
        {
            _pending.TrySetResult(null);
            _pending = null;
        }

        return _pending.Task;
    }

    /// <summary>由 <c>MainActivity</c> 转发 <c>OnActivityResult</c>。</summary>
    public static void HandleResult(Result resultCode, Intent? data)
    {
        var pending = _pending;
        if (pending is null)
        {
            return;
        }

        _pending = null;

        var uri = data?.Data;
        if (resultCode != Result.Ok || uri is null)
        {
            pending.TrySetResult(null);
            return;
        }

        var context = Platform.CurrentActivity;
        if (context is null)
        {
            pending.TrySetResult(null);
            return;
        }

        try
        {
            var flags = (ActivityFlags)((int)(data!.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission)));
            context.ContentResolver?.TakePersistableUriPermission(uri, flags);
        }
        catch (Exception)
        {
            // 部分提供方不支持持久化授权，忽略
        }

        try
        {
            if (_forSaving)
            {
                // 保存位置必须是真实可写路径（否则 Core 的 FileStream 写不进去）
                pending.TrySetResult(TryResolveFileSystemPath(uri));
                return;
            }

            // 发送：统一把整棵树拷到应用缓存再发送。
            // 这样不依赖 MANAGE_EXTERNAL_STORAGE 是否已授权，SAF 授权的目录也一定能读。
            pending.TrySetResult(CopyTreeToCache(context, uri));
        }
        catch (Exception)
        {
            pending.TrySetResult(null);
        }
    }

    private static string? TryResolveFileSystemPath(Android.Net.Uri treeUri)
    {
        var documentId = DocumentsContract.GetTreeDocumentId(treeUri);
        if (string.IsNullOrEmpty(documentId) || !documentId.StartsWith("primary:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = documentId["primary:".Length..].TrimStart('/');
        var root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var full = string.IsNullOrEmpty(relative) ? root : Path.Combine(root, relative);
        return Directory.Exists(full) ? full : null;
    }

    /// <summary>把 SAF 目录树递归复制到应用缓存目录（发送文件夹用）。</summary>
    private static string CopyTreeToCache(Context context, Android.Net.Uri treeUri)
    {
        var cacheRoot = Path.Combine(FileSystem.CacheDirectory, "saf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);

        CopyChildren(context, treeUri, DocumentsContract.GetTreeDocumentId(treeUri), cacheRoot);
        return cacheRoot;
    }

    private static void CopyChildren(Context context, Android.Net.Uri treeUri, string parentDocumentId, string targetDirectory)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocumentId);
        var resolver = context.ContentResolver;
        if (childrenUri is null || resolver is null)
        {
            return;
        }

        using var cursor = resolver.Query(
            childrenUri,
            new[]
            {
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName,
                DocumentsContract.Document.ColumnMimeType
            },
            null,
            null,
            null);

        if (cursor is null)
        {
            return;
        }

        while (cursor.MoveToNext())
        {
            var documentId = cursor.GetString(0);
            var displayName = cursor.GetString(1) ?? "file";
            var mimeType = cursor.GetString(2);
            if (string.IsNullOrEmpty(documentId))
            {
                continue;
            }

            var safeName = Core.Common.FileNameHelper.SanitizeFileName(displayName);
            var childUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
            if (childUri is null)
            {
                continue;
            }

            if (string.Equals(mimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal))
            {
                var subDirectory = Path.Combine(targetDirectory, safeName);
                Directory.CreateDirectory(subDirectory);
                CopyChildren(context, treeUri, documentId, subDirectory);
                continue;
            }

            try
            {
                using var input = resolver.OpenInputStream(childUri);
                if (input is null)
                {
                    continue;
                }

                using var output = File.Create(Path.Combine(targetDirectory, safeName));
                input.CopyTo(output);
            }
            catch (Exception)
            {
                // 单个文件失败不影响其他文件
            }
        }
    }
}
#endif