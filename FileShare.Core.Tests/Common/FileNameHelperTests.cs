using System.IO;
using System.Linq;
using FileShare.Core.Common;
using FileShare.Core.Models;

namespace FileShare.Core.Tests.Common;

/// <summary>
/// 文件名/相对路径安全处理测试（防目录穿越、防覆盖）。
/// </summary>
public class FileNameHelperTests
{
    [Theory]
    [InlineData("报告.pdf", "报告.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("trailing...", "trailing")]
    public void SanitizeFileName_StripsPathsAndIllegalChars(string input, string expected)
    {
        Assert.Equal(expected, FileNameHelper.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_ReplacesPlatformIllegalCharacters()
    {
        // 非法字符集是平台相关的：Windows 有 <>:"|?*，Linux/macOS 只有 '/' 和 '\0'。
        // 测试跟着平台走，否则在 Linux 上跑会误报（那些字符本来就合法，不该被替换）。
        var illegal = Path.GetInvalidFileNameChars()
            .Where(c => c != '/' && c != '\\' && !char.IsControl(c))
            .Distinct()
            .ToArray();

        if (illegal.Length == 0)
        {
            return;   // 当前平台除路径分隔符外没有非法字符，无需断言
        }

        var input = "a" + new string(illegal) + "b.txt";
        var result = FileNameHelper.SanitizeFileName(input);

        foreach (var ch in illegal)
        {
            Assert.DoesNotContain(ch, result);
        }

        Assert.EndsWith(".txt", result);
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongNamesKeepingExtension()
    {
        var name = new string('x', 400) + ".pdf";

        var result = FileNameHelper.SanitizeFileName(name);

        Assert.True(result.Length <= FileNameHelper.MaxFileNameLength);
        Assert.EndsWith(".pdf", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    public void SanitizeFileName_UsesFallbackForEmptyInput(string? input)
    {
        Assert.Equal("fallback.txt", FileNameHelper.SanitizeFileName(input, "fallback.txt"));
    }

    [Theory]
    [InlineData(@"照片\2024\a.jpg", "照片/2024/a.jpg")]
    [InlineData("照片/2024/a.jpg", "照片/2024/a.jpg")]
    [InlineData("a//b/./c.txt", "a/b/c.txt")]
    [InlineData("./a.txt", "a.txt")]
    public void SanitizeRelativePath_NormalizesSeparators(string input, string expected)
    {
        // 实现按平台的分隔符拼接（Windows '\\'、Linux '/'），期望值统一用 '/' 写再按平台转换
        var expectedNative = expected.Replace('/', Path.DirectorySeparatorChar);
        Assert.Equal(expectedNative, FileNameHelper.SanitizeRelativePath(input));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("")]
    [InlineData(null)]
    public void SanitizeRelativePath_RejectsUnsafeInput(string? input)
    {
        Assert.Null(FileNameHelper.SanitizeRelativePath(input));
    }

    [Fact]
    public void ResolveTargetPath_ReturnsSamePathWhenNotExists()
    {
        var dir = CreateTempDirectory();
        try
        {
            var path = FileNameHelper.ResolveTargetPath(dir, "a.txt", FileNameConflictPolicy.Rename);

            Assert.Equal(Path.Combine(dir, "a.txt"), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTargetPath_RenamesExistingFile()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "1");

            var path = FileNameHelper.ResolveTargetPath(dir, "a.txt", FileNameConflictPolicy.Rename);

            Assert.Equal(Path.Combine(dir, "a (1).txt"), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveTargetPath_OverwritePolicyKeepsPath()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "1");

            var path = FileNameHelper.ResolveTargetPath(dir, "a.txt", FileNameConflictPolicy.Overwrite);

            Assert.Equal(Path.Combine(dir, "a.txt"), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsUnderDirectory_DetectsTraversal()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.True(FileNameHelper.IsUnderDirectory(root, Path.Combine(root, "a", "b.txt")));
            Assert.False(FileNameHelper.IsUnderDirectory(root, Path.Combine(root, "..", "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fs-filehelper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
