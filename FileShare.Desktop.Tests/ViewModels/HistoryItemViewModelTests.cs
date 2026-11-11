using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// 历史条目展示模型：实体的派生属性（本地时间换算、消息标记、标题合并等）下沉到 VM 后，
/// 这里验证换算与展示语义。UTC 换算曾实现在实体上（2026-11-08 的时区修复），迁移后必须保持行为不变。
/// </summary>
public class HistoryItemViewModelTests
{
    [Fact]
    public void CreatedAtLocal_ConvertsUtcForDisplay()
    {
        var utc = new DateTime(2026, 11, 8, 6, 0, 0, DateTimeKind.Utc);
        var item = Make(new ReceiveHistoryEntity { CreatedAt = utc, SenderId = "p", FileName = "a.txt", FileSize = 1, SavePath = "" });

        Assert.Equal(utc.ToLocalTime(), item.CreatedAtLocal);
        Assert.Equal(DateTimeKind.Local, item.CreatedAtLocal.Kind);

        // Dapper 从 SQLite 读回的 DateTime.Kind 是 Unspecified，仍要按 UTC 换算
        var item2 = Make(new ReceiveHistoryEntity { CreatedAt = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), SenderId = "p", FileName = "a.txt", FileSize = 1, SavePath = "" });
        Assert.Equal(utc.ToLocalTime(), item2.CreatedAtLocal);
    }

    [Fact]
    public void MessageRecord_IsMessage_PreviewAndTitleUseContent()
    {
        var longText = new string('x', 120);
        var item = Make(new ReceiveHistoryEntity
        {
            SenderId = "p",
            FileName = "文本消息",
            FileSize = 120,
            SavePath = "",
            Message = longText
        });

        Assert.True(item.IsMessage);
        Assert.False(item.IsFileRecord);
        Assert.False(item.HasFile, "消息记录没有可打开的文件");
        Assert.StartsWith(new string('x', 100), item.MessagePreview);
        Assert.EndsWith("…", item.MessagePreview);
        Assert.Equal(item.MessagePreview, item.DisplayTitle);
        Assert.True(item.HasError == false);
    }

    [Fact]
    public void FileRecord_TitleIsFileName_StatusTextLocalized()
    {
        var item = Make(new ReceiveHistoryEntity
        {
            SenderId = "p",
            FileName = "photo.jpg",
            FileSize = 10,
            SavePath = "",
            Status = TransferStatus.Failed,
            ErrorMessage = "连接中断"
        });

        Assert.False(item.IsMessage);
        Assert.Equal("photo.jpg", item.DisplayTitle);
        Assert.False(item.IsSuccess);
        Assert.Equal("失败", item.StatusText);
        Assert.True(item.HasError);
    }

    [Fact]
    public void ResolveLocalFile_PrefersAbsolutePath_FallsBackToLegacyPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-histvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var legacyPath = Path.Combine(dir, "legacy.txt");
            File.WriteAllText(legacyPath, "content");

            // 只有 SavePath + FileName（旧记录）
            var item = Make(new ReceiveHistoryEntity
            {
                SenderId = "p", FileName = "legacy.txt", FileSize = 1,
                SavePath = dir
            });
            Assert.Equal(legacyPath, item.ResolveLocalFile());
            Assert.True(item.HasFile);

            // FilePath 指向不存在的文件时回退到旧路径
            var item2 = Make(new ReceiveHistoryEntity
            {
                SenderId = "p", FileName = "legacy.txt", FileSize = 1,
                SavePath = dir, FilePath = Path.Combine(dir, "missing.txt")
            });
            Assert.Equal(legacyPath, item2.ResolveLocalFile());

            // 都不存在 → null，HasFile 为 false
            var item3 = Make(new ReceiveHistoryEntity
            {
                SenderId = "p", FileName = "gone.txt", FileSize = 1,
                SavePath = dir
            });
            Assert.Null(item3.ResolveLocalFile());
            Assert.False(item3.HasFile);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PeerDisplayName_FallsBackThroughNameChain()
    {
        var item = Make(new ReceiveHistoryEntity
        {
            SenderId = "peer-id",
            FileName = "a.txt",
            FileSize = 1,
            SavePath = ""
        });
        Assert.Equal("peer-id", item.PeerDisplayName);

        var item2 = Make(new ReceiveHistoryEntity
        {
            SenderId = "peer-id",
            SenderDeviceName = "手机",
            FileName = "a.txt",
            FileSize = 1,
            SavePath = ""
        });
        Assert.Equal("手机", item2.PeerDisplayName);
    }

    private static HistoryItemViewModel Make(ReceiveHistoryEntity entity) =>
        HistoryItemViewModel.FromEntity(entity);
}
